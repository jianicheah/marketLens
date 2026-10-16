using System.Globalization;
using System.Text.Json;
using MarketLens.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MarketLens.Services;

// Official API connector. This application requests daily split-adjusted OHLCV only.
public sealed class TwelveDataProvider(IHttpClientFactory clients, IMemoryCache cache, TimeProvider clock, IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset minuteStart;
    private DateOnly creditDay;
    private int minuteRequests;
    private int dailyRequests;
    private long retryAfterTicks;
    private DateTimeOffset retryAfter
    {
        get => new(Interlocked.Read(ref retryAfterTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref retryAfterTicks, value.UtcTicks);
    }
    private static readonly CompanySearchResult[] Catalogue =
    [
        new("US.AAPL", "AAPL", "Apple Inc.", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.MSFT", "MSFT", "Microsoft Corporation", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.NVDA", "NVDA", "NVIDIA Corporation", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.AMZN", "AMZN", "Amazon.com Inc.", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.GOOGL", "GOOGL", "Alphabet Inc. Class A", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.META", "META", "Meta Platforms Inc.", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.TSLA", "TSLA", "Tesla Inc.", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), new("US.JPM", "JPM", "JPMorgan Chase & Co.", "NYSE", ExchangeMic: "XNYS", Currency: "USD"), new("US.V", "V", "Visa Inc.", "NYSE", ExchangeMic: "XNYS", Currency: "USD"), new("US.WMT", "WMT", "Walmart Inc.", "NYSE", ExchangeMic: "XNYS", Currency: "USD")
    ];

    public static CompanySearchResult[] Popular => Catalogue;
    public bool Configured => !string.IsNullOrWhiteSpace(configuration["MarketData:TwelveDataApiKey"]);

    public async Task<StockQuoteSnapshot> FetchQuoteAsync(CompanySearchResult listing, CancellationToken cancellation)
    {
        if (!DataValidation.ValidSymbol(listing.Symbol) || string.IsNullOrWhiteSpace(listing.ExchangeMic))
            throw new InvalidDataException("Choose a listed company with a known exchange.");
        var path = "quote?interval=1min&symbol=" + Uri.EscapeDataString(listing.ProviderSymbol) + "&mic_code=" + Uri.EscapeDataString(listing.ExchangeMic);
        return ParseQuote(await RequestAsync(path, cancellation), listing, clock.GetUtcNow());
    }

    public static StockQuoteSnapshot ParseQuote(JsonElement root, CompanySearchResult listing, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "symbol"), listing.ProviderSymbol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(root, "mic_code"), listing.ExchangeMic, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The intraday quote belongs to a different stock or exchange.");
        var currency = Text(root, "currency");
        if (string.IsNullOrWhiteSpace(currency) || listing.Currency is { Length: > 0 } knownCurrency && !string.Equals(currency, knownCurrency, StringComparison.Ordinal))
            throw new InvalidDataException("The intraday quote currency does not match this listing.");
        if (!decimal.TryParse(Text(root, "close"), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0 ||
            !root.TryGetProperty("timestamp", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var timestamp) || timestamp <= 0 || timestamp > now.AddMinutes(2).ToUnixTimeSeconds())
            throw new InvalidDataException("The intraday quote has no valid price or provider timestamp.");
        var asOf = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        bool? open = root.TryGetProperty("is_market_open", out var marketOpen) && marketOpen.ValueKind is JsonValueKind.True or JsonValueKind.False ? marketOpen.GetBoolean() : null;
        return new(listing.Symbol, price, currency, asOf, now, open);
    }

    public async Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 80) return [];
        var cacheKey = "twelve-search:" + query.ToUpperInvariant();
        if (cache.TryGetValue<CompanySearchResult[]>(cacheKey, out var cached)) return cached!;
        var response = await RequestAsync("symbol_search?symbol=" + Uri.EscapeDataString(query), cancellation);
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Twelve Data did not return company search results.");
        var results = data.EnumerateArray().Select(ParseListing).Where(x => x is not null).Cast<CompanySearchResult>().DistinctBy(x => x.Symbol).Take(24).ToArray();
        foreach (var company in results) cache.Set("twelve-company:" + company.Symbol, company, CacheEntry());
        cache.Set(cacheKey, results, CacheEntry()); return results;
    }

    public async Task<MarketData> FetchAsync(string symbol, CancellationToken cancellation, CompanySearchResult? listing = null)
    {
        if (!DataValidation.ValidSymbol(symbol)) throw new InvalidDataException("Choose a company from the website list or provider search.");
        var cacheKey = "twelve-data:" + symbol;
        if (cache.TryGetValue<MarketData>(cacheKey, out var cached)) return cached!;
        if (listing is not null && listing.Symbol != symbol) throw new InvalidDataException("The catalogue listing does not match the requested stock.");
        var company = listing ?? await ResolveAsync(symbol, cancellation);
        var path = "time_series?interval=1day&outputsize=365&adjust=splits&symbol=" + Uri.EscapeDataString(company.ProviderSymbol);
        if (!string.IsNullOrWhiteSpace(company.ExchangeMic)) path += "&mic_code=" + Uri.EscapeDataString(company.ExchangeMic);
        var response = await RequestAsync(path, cancellation);
        var result = Parse(response, company, clock.GetUtcNow());
        cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) }); return result;
    }

    private async Task<CompanySearchResult> ResolveAsync(string symbol, CancellationToken cancellation)
    {
        if (cache.TryGetValue<CompanySearchResult>("twelve-company:" + symbol, out var cached)) return cached!;
        var known = Popular.FirstOrDefault(x => x.Symbol == symbol); if (known is not null) return known;
        var code = symbol[(symbol.IndexOf('.') + 1)..];
        return (await SearchAsync(code, cancellation)).FirstOrDefault(x => x.Symbol == symbol) ?? throw new InvalidDataException("Twelve Data did not return this exact exchange listing.");
    }

    private async Task<JsonElement> RequestAsync(string path, CancellationToken cancellation)
    {
        var key = configuration["MarketData:TwelveDataApiKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Set MarketData__TwelveDataApiKey before using Twelve Data.");
        await gate.WaitAsync(cancellation);
        try
        {
            var now = clock.GetUtcNow();
            if (now < retryAfter) throw new InvalidDataException("Twelve Data requests are temporarily paused after a rate limit. Please try again later.");
            if (now - minuteStart >= TimeSpan.FromMinutes(1)) { minuteStart = now; minuteRequests = 0; }
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (today != creditDay) { creditDay = today; dailyRequests = 0; }
            var minuteLimit = Math.Clamp(configuration.GetValue("MarketData:TwelveDataCreditsPerMinute", 8), 1, 100000);
            var dailyLimit = Math.Clamp(configuration.GetValue("MarketData:TwelveDataCreditsPerDay", 800), 1, int.MaxValue);
            if (minuteRequests >= minuteLimit || dailyRequests >= dailyLimit)
                throw new InvalidDataException("The website's shared Twelve Data request budget is exhausted. Cached data may remain available; please try again later.");
            minuteRequests++; dailyRequests++;
        }
        finally { gate.Release(); }
        // Reserve credits under the lock; network latency must not block other requests.
        try
        {
            using var response = await clients.CreateClient("TwelveData").GetAsync(path + (path.Contains('?') ? "&" : "?") + "apikey=" + Uri.EscapeDataString(key), cancellation);
            if ((int)response.StatusCode == 429) { retryAfter = clock.GetUtcNow().AddMinutes(1); throw new InvalidDataException("Twelve Data rate or daily-credit limit was reached. Please try again later."); }
            var body = await response.Content.ReadAsStringAsync(cancellation);
            using var document = JsonDocument.Parse(body);
            var providerError = document.RootElement.TryGetProperty("status", out var status) && status.GetString()?.Equals("error", StringComparison.OrdinalIgnoreCase) == true;
            if (providerError)
            {
                var message = Text(document.RootElement, "message") ?? "request rejected.";
                if (message.Contains("upgrad", StringComparison.OrdinalIgnoreCase) || message.Contains("plan", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Your Twelve Data plan does not include the requested data for this company or exchange.");
                if (message.Contains("limit", StringComparison.OrdinalIgnoreCase) || message.Contains("credit", StringComparison.OrdinalIgnoreCase))
                    retryAfter = clock.GetUtcNow().AddMinutes(1);
                message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
                throw new InvalidDataException("Twelve Data: " + message);
            }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException((int)response.StatusCode is 401 or 403 ? "Twelve Data rejected the API key or this plan's access." : "Twelve Data is unavailable. Try again later.");
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new InvalidDataException("The Twelve Data connection failed. No prices were retrieved."); }
    }

    public static MarketData Parse(JsonElement root, CompanySearchResult company, DateTimeOffset now)
    {
        if (!root.TryGetProperty("meta", out var meta) || !root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0) throw new InvalidDataException("Twelve Data supplied no daily prices for this company.");
        if (!string.Equals(Text(meta, "symbol"), company.ProviderSymbol, StringComparison.OrdinalIgnoreCase) ||
            Text(meta, "mic_code") is { } responseMic && company.ExchangeMic is { } listingMic && responseMic != listingMic)
            throw new InvalidDataException("Twelve Data returned prices for a different stock or exchange.");
        var currency = Text(meta, "currency") ?? company.Currency ?? DataValidation.Currency(company.Symbol); if (currency is not ("GBp" or "ZAc")) currency = currency.ToUpperInvariant();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(DataValidation.ZoneId(company.Symbol));
        var bars = values.EnumerateArray().Select(row =>
        {
            if (!DateTime.TryParse(Text(row, "datetime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidDataException("Twelve Data returned an invalid trading date.");
            decimal Number(string name) => decimal.TryParse(Text(row, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException("Twelve Data returned an incomplete price bar.");
            var localClose = DateTime.SpecifyKind(date.Date.AddHours(16), DateTimeKind.Unspecified);
            return new PriceBar(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localClose, zone)), Number("open"), Number("high"), Number("low"), Number("close"), Number("volume"));
        }).OrderBy(x => x.Time).ToArray();
        var data = new MarketData(company.Symbol, Text(meta, "name") ?? company.Name, currency, "Daily market data via Twelve Data", now, bars, [], SplitAdjusted: true, Warnings: ["End-of-day research only. Intraday snapshots are displayed separately; no verified bid/ask or actionable day-trading data is supplied to this analysis.", "Daily values are requested with Twelve Data split adjustment. Price history is cached for 24 hours."]);
        DataValidation.Validate(data, now); return data;
    }

    private static CompanySearchResult? ParseListing(JsonElement row)
    {
        var ticker = Text(row, "symbol")?.ToUpperInvariant(); var mic = Text(row, "mic_code"); var market = mic is null ? null : MarketDirectory.MarketFor(mic, CountryCode(Text(row, "country"))); var name = Text(row, "instrument_name") ?? Text(row, "name");
        if (market is null || ticker is null || name is null) return null;
        var symbol = market + "." + ticker;
        return DataValidation.ValidSymbol(symbol) ? new CompanySearchResult(symbol, ticker, name, Text(row, "exchange") ?? mic!, ExchangeMic: mic, Currency: Text(row, "currency")) : null;
    }
    private static string? CountryCode(string? country) => country?.Trim().ToUpperInvariant() switch { "UNITED STATES" or "US" => "US", "MALAYSIA" or "MY" => "MY", "HONG KONG" or "HK" => "HK", "SINGAPORE" or "SG" => "SG", "JAPAN" or "JP" => "JP", "UNITED KINGDOM" or "GB" => "GB", "AUSTRALIA" or "AU" => "AU", "CANADA" or "CA" => "CA", "CHINA" or "CN" => "CN", "INDIA" or "IN" => "IN", "SOUTH KOREA" or "KR" => "KR", _ => country };
    private static string? Text(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static MemoryCacheEntryOptions CacheEntry() => new() { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) };
}
