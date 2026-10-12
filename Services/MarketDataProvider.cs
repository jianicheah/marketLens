using MarketLens.Models;
namespace MarketLens.Services;

public sealed class MarketDataProvider(PublicMarketDataProvider yahoo, MarketstackDataProvider marketstack, IConfiguration configuration, AvailableCompanyCatalog available)
{
    public bool UsesMarketstack => string.Equals(configuration["MarketData:Provider"], "Marketstack", StringComparison.OrdinalIgnoreCase);
    public bool Configured => !UsesMarketstack || marketstack.Configured;
    public CompanySearchResult[] Popular => AvailableCompanies().Take(10).ToArray();
    // The website catalogue exists independently of the short-lived price cache.
    public CompanySearchResult[] AvailableCompanies(string? country = null, string? query = null) =>
        (UsesMarketstack ? MarketstackDataProvider.Popular : Array.Empty<CompanySearchResult>())
        .Concat(available.Companies(Name)).DistinctBy(x => x.Symbol)
        .Where(x => country is null || AvailableCompanyCatalog.CountryCode(x.Symbol) == country)
        .Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || x.Symbol.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Name).ToArray();
    public AvailableCountry[] AvailableCountries => Configured ? available.Countries(Name) : [];
    public MarketData? ReadAvailable(string symbol) => Configured ? available.Read(Name, symbol) : null;
    public bool HasPrices(string symbol) => Configured && available.Read(Name, symbol) is not null;
    public Task<CompanySearchResult[]> BrowseAsync(string mic, int offset, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync("", cancellation, mic, offset) : yahoo.SearchAsync(MarketDirectory.Find(mic)?.Market ?? "", cancellation);
    public Task<CompanySearchResult[]> FindAsync(string query, string? mic, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync(query, cancellation, mic) : yahoo.SearchAsync(query, cancellation);
    public string Name => UsesMarketstack ? "Marketstack" : "Yahoo Finance";
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.SearchAsync(query, cancellation) : yahoo.SearchAsync(query, cancellation);
    private readonly SemaphoreSlim visitorGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> failedUntil = new();
    public async Task<MarketData> FetchListedAsync(string symbol, CancellationToken cancellation)
    {
        // Anonymous visitors can open the finite website list, never arbitrary provider symbols/searches.
        if (!AvailableCompanies().Any(x => x.Symbol == symbol))
            throw new InvalidDataException("This company is not in the website's company list.");
        if (!Configured)
            throw new InvalidDataException("Daily prices are unavailable because the website data connection is not configured.");
        await visitorGate.WaitAsync(cancellation);
        try
        {
            var cached = ReadAvailable(symbol);
            if (cached is not null) return cached;
            foreach (var expired in failedUntil.Where(x => x.Value <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                failedUntil.Remove(expired);
            if (failedUntil.TryGetValue(symbol, out var until) && until > DateTimeOffset.UtcNow)
                throw new InvalidDataException("Daily prices for this company are temporarily unavailable. Please try again in a few minutes.");
            try { return await FetchAsync(symbol, false, cancellation); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                failedUntil[symbol] = DateTimeOffset.UtcNow.AddMinutes(5);
                throw;
            }
        }
        finally { visitorGate.Release(); }
    }
    public async Task<MarketData> FetchAsync(string symbol, bool includeIntraday, CancellationToken cancellation)
    {
        if (!Configured) throw new InvalidDataException("The data connection is not configured. Open Data sources to connect the provider.");
        var cached = available.Read(Name, symbol);
        if (UsesMarketstack && cached is not null) return cached;
        var data = UsesMarketstack ? await marketstack.FetchAsync(symbol, cancellation) : await yahoo.FetchAsync(symbol, includeIntraday, cancellation);
        available.Record(Name, data);
        return data;
    }
}
