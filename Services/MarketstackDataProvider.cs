using System.Globalization;
using System.Text.Json;
using MarketLens.Models;
using Microsoft.Extensions.Caching.Memory;
namespace MarketLens.Services;

public sealed class MarketstackDataProvider(IHttpClientFactory clients, IMemoryCache cache, TimeProvider clock, IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset retryAfter;
    // A bounded catalogue saves the free quota for historical prices rather than metadata searches.
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
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation)
    {
        query = query.Trim();
        return Task.FromResult(query.Length is < 1 or > 80 ? [] : Catalogue.Where(x =>
            x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
    public async Task<MarketData> FetchAsync(string symbol, CancellationToken cancellation)
    {
        var company = Catalogue.FirstOrDefault(x => x.Symbol == symbol) ?? throw new InvalidDataException(
            "The free personal version supports the 10 US starter stocks shown in search. Malaysia and other markets are not enabled.");
        var apiKey = configuration["MarketData:MarketstackApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidDataException(
            "Marketstack is not configured yet. The website owner must set MarketData__MarketstackApiKey in Render Environment and redeploy. Visitors do not need an API key.");
        var key = "marketstack:" + symbol;
        if (cache.TryGetValue<MarketData>(key, out var cached)) return cached!;
        await gate.WaitAsync(cancellation);
        try
        {
            if (cache.TryGetValue<MarketData>(key, out cached)) return cached!;
            if (clock.GetUtcNow() < retryAfter) throw new InvalidDataException("Marketstack requests are paused after a quota or rate-limit response. Retry later; no new data was retrieved.");
            var from = clock.GetUtcNow().AddDays(-364).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Marketstack documents query-string key authentication; HttpClient logging is disabled in Program.
            var path = "v2/eod?symbols=" + Uri.EscapeDataString(company.ProviderSymbol) + "&limit=1000&sort=DESC&date_from=" + from + "&access_key=" + Uri.EscapeDataString(apiKey);
            using var response = await clients.CreateClient("Marketstack").GetAsync(path, cancellation);
            if ((int)response.StatusCode == 429)
            {
                retryAfter = clock.GetUtcNow().AddMinutes(5);
                throw new InvalidDataException("Marketstack has reached a rate or monthly quota limit. The free plan allows 100 requests per month; check your provider dashboard.");
            }
            if (!response.IsSuccessStatusCode)
            {
                retryAfter = clock.GetUtcNow().AddMinutes(1);
                throw new InvalidDataException((int)response.StatusCode is 401 or 403
                    ? "Marketstack rejected the key or account permissions. Check your API key and free-plan access in its dashboard."
                    : "Marketstack could not supply data. Try later; no fictional fallback is used.");
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.TryGetProperty("error", out _))
            {
                retryAfter = clock.GetUtcNow().AddMinutes(5);
                throw new InvalidDataException("Marketstack returned an API error. Check your key, plan and remaining quota in its dashboard.");
            }
            var data = Parse(document.RootElement, company, clock.GetUtcNow());
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
            !x.TryGetProperty("exchange", out var exchange) || exchange.GetString() is not ("XNAS" or "XNYS")))
            throw new InvalidDataException("Marketstack returned a different symbol or an unsupported exchange.");
        var adjusted = rows.All(x => new[] { "adj_open", "adj_high", "adj_low", "adj_close" }.All(field => Number(x, field) is > 0));
        var prefix = adjusted ? "adj_" : "";
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
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
            "The provider trading date is represented at US session start; this is not the closing or retrieval timestamp."
        };
        if (adjusted) warnings.Add("Provider-adjusted OHLC is used; corporate-action adjustments may include dividends. Volume is provider-reported.");
        else warnings.Add("Adjusted OHLC is incomplete. Raw prices are shown, but trend signals and backtests are withheld.");
        var data = new MarketData(company.Symbol, company.Name, "USD", "Daily market data via Marketstack", now, bars, [],
            SplitAdjusted: adjusted, Warnings: warnings.ToArray());
        DataValidation.Validate(data, now);
        return data;
    }
    private static decimal? Number(JsonElement row, string field) => row.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
}
