using System.Globalization;
using System.Text.Json;
using MarketLens.Models;
using Microsoft.Extensions.Caching.Memory;
namespace MarketLens.Services;

public sealed class MarketstackDataProvider(IHttpClientFactory clients, IMemoryCache cache, TimeProvider clock, IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset retryAfter;
    // Popular shortcuts render without spending API requests; directory search is not limited to these suggestions.
    private static readonly CompanySearchResult[] Catalogue =
    [
        new("US.AAPL", "AAPL", "Apple Inc.", "NASDAQ"),
        new("US.MSFT", "MSFT", "Microsoft Corporation", "NASDAQ"),
        new("US.NVDA", "NVDA", "NVIDIA Corporation", "NASDAQ"),
        new("US.AMZN", "AMZN", "Amazon.com Inc.", "NASDAQ"),
        new("US.GOOGL", "GOOGL", "Alphabet Inc. Class A", "NASDAQ"),
        new("US.META", "META", "Meta Platforms Inc.", "NASDAQ"),
        new("US.TSLA", "TSLA", "Tesla Inc.", "NASDAQ"),
        new("US.JPM", "JPM", "JPMorgan Chase & Co.", "NYSE"),
        new("US.V", "V", "Visa Inc.", "NYSE"),
        new("US.WMT", "WMT", "Walmart Inc.", "NASDAQ")
    ];
    public static CompanySearchResult[] Popular => Catalogue.Select(x => x with
        { ExchangeMic = x.Exchange == "NYSE" ? "XNYS" : "XNAS", Currency = "USD" }).ToArray();
    public bool Configured => !string.IsNullOrWhiteSpace(configuration["MarketData:MarketstackApiKey"]);
    public bool HasPrices(string symbol) => cache.TryGetValue<MarketData>("marketstack:" + symbol, out _);
    public async Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation, string? mic = null, int offset = 0)
    {
        query = query.Trim();
        if (query.Length > 80 || offset < 0 || offset > 10000 || mic is not null && MarketDirectory.Find(mic) is null) return [];
        var key = $"marketstack-list:{mic}:{query.ToUpperInvariant()}:{offset}";
        if (cache.TryGetValue<CompanySearchResult[]>(key, out var cached)) return cached!;
        // Searching a familiar name can still work during initial setup; this is not the universe limit.
        if (!Configured) return Popular.Where(x => (mic is null || x.ExchangeMic == mic) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        await gate.WaitAsync(cancellation);
        try
        {
            if (cache.TryGetValue<CompanySearchResult[]>(key, out cached)) return cached!;
            // The listing directory uses v1; v2/tickers returns 404. Price history remains v2/eod.
            var path = "v1/tickers?limit=24&offset=" + offset;
            if (query.Length > 0) path += "&search=" + Uri.EscapeDataString(query);
            if (mic is not null) path += "&exchange=" + Uri.EscapeDataString(mic);
            var document = await RequestAsync(path, cancellation);
            var results = ParseListings(document).Where(x => mic is null || x.ExchangeMic == mic).ToArray();
            foreach (var listing in results)
                cache.Set("marketstack-company:" + listing.Symbol, listing, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
            cache.Set(key, results, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
            return results;
        }
        finally { gate.Release(); }
    }
    public static CompanySearchResult[] ParseListings(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The provider did not return a listing directory.");
        var listings = new List<CompanySearchResult>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("has_eod", out var eod) && eod.ValueKind == JsonValueKind.False) continue;
            if (!row.TryGetProperty("stock_exchange", out var exchange) || exchange.ValueKind != JsonValueKind.Object) continue;
            var mic = Text(exchange, "mic");
            var market = mic is null ? null : MarketDirectory.MarketFor(mic, Text(exchange, "country_code"));
            var ticker = Text(row, "symbol")?.ToUpperInvariant();
            var name = Text(row, "name");
            if (market is null || ticker is null || name is null || name.Length > 150) continue;
            var symbol = market + "." + ticker;
            if (!DataValidation.ValidSymbol(symbol)) continue;
            var currency = Text(row, "currency") ?? Text(exchange, "currency");
            listings.Add(new(symbol, ticker, name, Text(exchange, "name") ?? mic!, ExchangeMic: mic, Currency: currency));
        }
        return listings.DistinctBy(x => x.Symbol).ToArray();
    }
    private async Task<CompanySearchResult> ResolveAsync(string symbol, CancellationToken cancellation)
    {
        if (!DataValidation.ValidSymbol(symbol)) throw new InvalidDataException("Select a listing from Browse markets or search.");
        var known = Popular.FirstOrDefault(x => x.Symbol == symbol);
        if (known is not null) return known;
        if (cache.TryGetValue<CompanySearchResult>("marketstack-company:" + symbol, out var cached)) return cached!;
        if (!Configured) throw new InvalidDataException("The owner must configure the Marketstack API key before searching more listings.");
        var market = symbol[..symbol.IndexOf('.')];
        var code = symbol[(symbol.IndexOf('.') + 1)..];
        var exchanges = MarketDirectory.Choices.Where(x => x.Market == market).ToArray();
        var filter = exchanges.Length == 1 ? exchanges[0].Mic : null;
        var matches = await SearchAsync(code, cancellation, filter);
        // Exact ticker/exchange matching avoids opening a similarly named company in another country.
        var match = matches.FirstOrDefault(x => x.Symbol == symbol ||
            market == "HK" && x.Symbol.StartsWith("HK.", StringComparison.Ordinal) && code.All(char.IsAsciiDigit) && x.ProviderSymbol.All(char.IsAsciiDigit) && x.ProviderSymbol.TrimStart('0') == code.TrimStart('0'));
        return match is null ? throw new InvalidDataException("No matching daily-data listing was returned. Browse that market to choose the provider's exact listing; absence is not proof the whole market is unsupported.") : match with { Symbol = symbol };
    }
    private async Task<JsonElement> RequestAsync(string path, CancellationToken cancellation)
    {
        var apiKey = configuration["MarketData:MarketstackApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidDataException("The owner must set MarketData__MarketstackApiKey in Render Environment before fetching data.");
        if (clock.GetUtcNow() < retryAfter) throw new InvalidDataException("Provider requests are paused after a rate or quota limit. Check remaining quota and retry later.");
        try
        {
            using var response = await clients.CreateClient("Marketstack").GetAsync(path + "&access_key=" + Uri.EscapeDataString(apiKey), cancellation);
            if ((int)response.StatusCode == 429)
            {
                retryAfter = clock.GetUtcNow().AddMinutes(5);
                throw new InvalidDataException("Marketstack reached a rate or monthly quota limit. The free account allows 100 requests per month across browsing, search and prices.");
            }
            if (!response.IsSuccessStatusCode)
            {
                retryAfter = clock.GetUtcNow().AddMinutes(1);
                throw new InvalidDataException((int)response.StatusCode is 401 or 403 ? "The provider rejected the API key or this endpoint's plan access. Check the owner account." : "The provider is unavailable. Try later; no replacement prices are invented.");
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.TryGetProperty("error", out _))
            {
                retryAfter = clock.GetUtcNow().AddMinutes(5);
                throw new InvalidDataException("The provider returned an account, quota or access error. Check the owner dashboard.");
            }
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new InvalidDataException("The data connection failed. Try later; saved prices are unchanged."); }
    }
    private static string? Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async Task<MarketData> FetchAsync(string symbol, CancellationToken cancellation)
    {
        var key = "marketstack:" + symbol;
        if (cache.TryGetValue<MarketData>(key, out var cached)) return cached!;
        var company = await ResolveAsync(symbol, cancellation);
        await gate.WaitAsync(cancellation);
        try
        {
            if (cache.TryGetValue<MarketData>(key, out cached)) return cached!;
            var from = clock.GetUtcNow().AddDays(-364).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var path = "v2/eod?symbols=" + Uri.EscapeDataString(company.ProviderSymbol) + "&limit=1000&sort=DESC&date_from=" + from;
            if (company.ExchangeMic is not null) path += "&exchange=" + Uri.EscapeDataString(company.ExchangeMic);
            var response = await RequestAsync(path, cancellation);
            var data = Parse(response, company, clock.GetUtcNow());
            cache.Set(key, data, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            return data;
        }
        catch (HttpRequestException) { throw new InvalidDataException("The Marketstack connection failed. No new data was retrieved. Try later."); }
        finally { gate.Release(); }
    }
    public static MarketData Parse(JsonElement root, CompanySearchResult company, DateTimeOffset now)
    {
        if (!root.TryGetProperty("data", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            throw new InvalidDataException("Marketstack supplied no daily prices for this stock.");
        var rows = values.EnumerateArray().ToArray();
        if (rows.Any(x => !x.TryGetProperty("symbol", out var symbol) || symbol.GetString() != company.ProviderSymbol ||
            !x.TryGetProperty("exchange", out var exchange) || (company.ExchangeMic is not null ? exchange.GetString() != company.ExchangeMic : MarketDirectory.MarketFor(exchange.GetString() ?? "", null) != company.Symbol.Split('.')[0])))
            throw new InvalidDataException("Marketstack returned a different symbol or an unsupported exchange.");
        var allRows = rows;
        DateTimeOffset TradingDate(JsonElement row) => DateTimeOffset.TryParse(Text(row, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new InvalidDataException("Marketstack returned an invalid trading date.");
        bool Complete(JsonElement row, string prefix)
        {
            var open = Number(row, prefix + "open"); var high = Number(row, prefix + "high");
            var low = Number(row, prefix + "low"); var close = Number(row, prefix + "close"); var volume = Number(row, "volume");
            return open is > 0 && close is > 0 && low is > 0 && high is > 0 and <= 1000000000m &&
                high >= Math.Max(open.Value, close.Value) && low <= Math.Min(open.Value, close.Value) && volume is >= 0 and <= 1000000000000000m;
        }
        rows = rows.OrderByDescending(TradingDate).ToArray();
        var adjusted = Complete(rows[0], "adj_");
        // Stop at the first incomplete bar; never join older prices across a provider-data gap.
        rows = rows.TakeWhile(row => Complete(row, adjusted ? "adj_" : "")).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("The latest provider price bar is incomplete. No reliable current series can be shown.");
        var omitted = allRows.Length - rows.Length;
        var prefix = adjusted ? "adj_" : "";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(DataValidation.ZoneId(company.Symbol));
        var bars = rows.Select(x =>
        {
            // Provider date is a trading-day label at UTC midnight, not an actual closing timestamp.
            if (!DateTimeOffset.TryParse(x.GetProperty("date").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidDataException("Marketstack returned an invalid trading date.");
            var session = DateTime.SpecifyKind(date.Date.AddHours(9.5), DateTimeKind.Unspecified);
            var time = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(session, zone));
            decimal Required(string field) => Number(x, field) ?? throw new InvalidDataException("Marketstack returned an incomplete price bar.");
            return new PriceBar(time, Required(prefix + "open"), Required(prefix + "high"), Required(prefix + "low"), Required(prefix + "close"), Required("volume"));
        }).OrderBy(x => x.Time).ToArray();
        var warnings = new List<string>
        {
            "End-of-day research only; no live price, verified bid/ask or actionable day-trading data.",
            "The free connector does not supply fundamentals; long-term analysis is unavailable.",
            "Price history is cached for 24 hours to conserve the 100-request monthly quota. Render restarts clear this cache.",
            "The provider trading date uses a market-local date marker; this is not a verified closing or retrieval timestamp."
        };
        if (omitted > 0) warnings.Add($"History shortened: {omitted} older rows were excluded at the first incomplete price/volume bar. Only the complete recent segment is used; missing prices were not filled.");
        if (adjusted) warnings.Add("Provider-adjusted OHLC is used; corporate-action adjustments may include dividends. Volume is provider-reported.");
        else warnings.Add("Adjusted OHLC is incomplete. Raw prices are shown, but trend signals and backtests are withheld.");
        var currencies = allRows.Select(x => Text(x, "price_currency") ?? Text(x, "currency")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (currencies.Length > 1) throw new InvalidDataException("The provider returned inconsistent price currencies.");
        var currency = currencies.SingleOrDefault() ?? company.Currency;
        if (currencies.Length == 0 && (company.Symbol.StartsWith("UK.") || company.Symbol.StartsWith("ZA.")))
            throw new InvalidDataException("Price currency/unit is missing; this listing cannot yet be analysed safely.");
        currency ??= DataValidation.Currency(company.Symbol);
        if (currency is not ("GBp" or "ZAc")) currency = currency.ToUpperInvariant();
        var data = new MarketData(company.Symbol, company.Name, currency, "Daily market data via Marketstack", now, bars, [],
            SplitAdjusted: adjusted, Warnings: warnings.ToArray());
        DataValidation.Validate(data, now);
        return data;
    }
    private static decimal? Number(JsonElement row, string field) => row.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
}
