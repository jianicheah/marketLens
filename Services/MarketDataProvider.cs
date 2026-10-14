using MarketLens.Models;
namespace MarketLens.Services;

public sealed class MarketDataProvider(PublicMarketDataProvider yahoo, MarketstackDataProvider marketstack, TwelveDataProvider twelve, IConfiguration configuration, AvailableCompanyCatalog available)
{
    private readonly Lazy<CompanySearchResult[]> savedCatalogue = new(() => WebsiteCompanyCatalog.Read(
        configuration["MarketData:CompanyCatalogPath"] ?? Path.Combine("Catalogue",
            string.Equals(configuration["MarketData:Provider"]?.Trim(), "Marketstack", StringComparison.OrdinalIgnoreCase)
                ? "company-catalogue.json" : "company-catalogue-twelvedata.json")));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompanySearchResult> directory = new();
    public async Task<ListingPage> DiscoverAsync(string? query, string? mic, int offset, CancellationToken cancellation)
    {
        var page = await marketstack.SearchPageAsync(query ?? "", cancellation, mic, offset);
        foreach (var company in page.Companies)
        {
            if (directory.Count >= 10000 && !directory.ContainsKey(company.Symbol))
            {
                var oldest = directory.Keys.FirstOrDefault(x => !page.Companies.Any(c => c.Symbol == x));
                if (oldest is not null) directory.TryRemove(oldest, out _);
            }
            directory[company.Symbol] = company;
        }
        return page;
    }
    public bool UsesMarketstack => Provider == "Marketstack";
    public bool UsesTwelveData => Provider == "TwelveData";
    public bool UsesManagedProvider => UsesMarketstack || UsesTwelveData;
    public bool DailyOnly => UsesManagedProvider;
    private string Provider => (configuration["MarketData:Provider"] ?? "TwelveData").Trim().ToUpperInvariant() switch
    {
        "MARKETSTACK" => "Marketstack",
        "YAHOO" or "YAHOOFINANCE" => "Yahoo",
        _ => "TwelveData"
    };
    public bool Configured => UsesMarketstack ? marketstack.Configured : UsesTwelveData ? twelve.Configured : true;
    public CompanySearchResult[] Popular => AvailableCompanies().Take(10).ToArray();
    public CompanySearchResult[] AvailableCompanies(string? country = null, string? query = null) =>
        (UsesManagedProvider ? savedCatalogue.Value : [])
        .Concat(UsesMarketstack ? MarketstackDataProvider.Popular : UsesTwelveData ? TwelveDataProvider.Popular : Array.Empty<CompanySearchResult>())
        .Concat(UsesMarketstack ? directory.Values : Enumerable.Empty<CompanySearchResult>())
        .Concat(available.Companies(Name)).DistinctBy(x => x.Symbol)
        .Where(x => country is null || AvailableCompanyCatalog.CountryCode(x.Symbol) == country)
        .Where(x => string.IsNullOrWhiteSpace(query) || Matches(x, query.Trim()))
        .OrderBy(x => x.Name).ToArray();
    private static bool Matches(CompanySearchResult company, string query)
    {
        var aliases = company.Symbol switch
        {
            "MY.1155" => "Maybank", "MY.5099" => "AirAsia Capital A",
            "US.GOOGL" or "US.GOOG" => "Google", _ => ""
        };
        return company.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            company.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            aliases.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    public AvailableCountry[] AvailableCountries => Configured ? available.Countries(Name) : [];
    public MarketData? ReadAvailable(string symbol) => Configured ? available.Read(Name, symbol) : null;
    public bool HasPrices(string symbol) => Configured && available.Read(Name, symbol) is not null;
    public Task<CompanySearchResult[]> BrowseAsync(string mic, int offset, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync("", cancellation, mic, offset) : Task.FromResult(Array.Empty<CompanySearchResult>());
    public Task<CompanySearchResult[]> FindAsync(string query, string? mic, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync(query, cancellation, mic) : SearchAsync(query, cancellation);
    public string Name => UsesMarketstack ? "Marketstack" : UsesTwelveData ? "Twelve Data" : "Yahoo Finance";
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync(query, cancellation) : UsesTwelveData ? twelve.SearchAsync(query, cancellation) : yahoo.SearchAsync(query, cancellation);
    private readonly SemaphoreSlim visitorGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Until, string Reason)> failedUntil = new();
    public async Task<MarketData> FetchListedAsync(string symbol, CancellationToken cancellation)
    {
        if (!AvailableCompanies().Any(x => x.Symbol == symbol)) throw new InvalidDataException("This company is not in the website's company list.");
        if (!Configured) throw new InvalidDataException("Daily prices are unavailable because the website data connection is not configured.");
        await visitorGate.WaitAsync(cancellation);
        try
        {
            var cached = ReadAvailable(symbol); if (cached is not null) return cached;
            foreach (var expired in failedUntil.Where(x => x.Value.Until <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) failedUntil.Remove(expired);
            if (failedUntil.TryGetValue(symbol, out var failure) && failure.Until > DateTimeOffset.UtcNow) throw new InvalidDataException(failure.Reason + " Retry available at " + failure.Until.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss 'SGT'") + ".");
            try { return await FetchAsync(symbol, false, cancellation); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            { var reason = e is InvalidDataException ? e.Message : "The daily-price request failed. Check the data connection."; failedUntil[symbol] = (DateTimeOffset.UtcNow.AddMinutes(5), reason); throw new InvalidDataException(reason); }
        }
        finally { visitorGate.Release(); }
    }
    public async Task<MarketData> FetchAsync(string symbol, bool includeIntraday, CancellationToken cancellation)
    {
        if (!Configured) throw new InvalidDataException("The data connection is not configured. Open Data sources to connect the provider.");
        var cached = available.Read(Name, symbol); if (UsesManagedProvider && cached is not null) return cached;
        var listing = UsesTwelveData ? AvailableCompanies().FirstOrDefault(x => x.Symbol == symbol) : null;
        var data = UsesMarketstack ? await marketstack.FetchAsync(symbol, cancellation) : UsesTwelveData ? await twelve.FetchAsync(symbol, cancellation, listing) : await yahoo.FetchAsync(symbol, includeIntraday, cancellation);
        available.Record(Name, data); return data;
    }
}
