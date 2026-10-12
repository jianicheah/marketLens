using MarketLens.Models;
namespace MarketLens.Services;

public sealed class MarketDataProvider(PublicMarketDataProvider yahoo, MarketstackDataProvider marketstack, IConfiguration configuration, AvailableCompanyCatalog available)
{
    public bool UsesMarketstack => string.Equals(configuration["MarketData:Provider"], "Marketstack", StringComparison.OrdinalIgnoreCase);
    public bool Configured => !UsesMarketstack || marketstack.Configured;
    public CompanySearchResult[] Popular => AvailableCompanies().Take(10).ToArray();
    public CompanySearchResult[] AvailableCompanies(string? country = null, string? query = null) => Configured ? available.Companies(Name, country, query) : [];
    public AvailableCountry[] AvailableCountries => Configured ? available.Countries(Name) : [];
    public bool HasPrices(string symbol) => Configured && available.Read(Name, symbol) is not null;
    public Task<CompanySearchResult[]> BrowseAsync(string mic, int offset, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync("", cancellation, mic, offset) : yahoo.SearchAsync(MarketDirectory.Find(mic)?.Market ?? "", cancellation);
    public Task<CompanySearchResult[]> FindAsync(string query, string? mic, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync(query, cancellation, mic) : yahoo.SearchAsync(query, cancellation);
    public string Name => UsesMarketstack ? "Marketstack" : "Yahoo Finance";
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.SearchAsync(query, cancellation) : yahoo.SearchAsync(query, cancellation);
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
