using MarketLens.Models;
namespace MarketLens.Services;

public sealed class MarketDataProvider(PublicMarketDataProvider yahoo, MarketstackDataProvider marketstack, IConfiguration configuration)
{
    public bool UsesMarketstack => string.Equals(configuration["MarketData:Provider"], "Marketstack", StringComparison.OrdinalIgnoreCase);
    public bool Configured => !UsesMarketstack || marketstack.Configured;
    public CompanySearchResult[] Popular => MarketstackDataProvider.Popular;
    public bool HasPrices(string symbol) => UsesMarketstack && marketstack.HasPrices(symbol);
    public Task<CompanySearchResult[]> BrowseAsync(string mic, int offset, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync("", cancellation, mic, offset) : yahoo.SearchAsync(MarketDirectory.Find(mic)?.Market ?? "", cancellation);
    public Task<CompanySearchResult[]> FindAsync(string query, string? mic, CancellationToken cancellation) => UsesMarketstack ? marketstack.SearchAsync(query, cancellation, mic) : yahoo.SearchAsync(query, cancellation);
    public string Name => UsesMarketstack ? "Marketstack" : "Yahoo Finance";
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.SearchAsync(query, cancellation) : yahoo.SearchAsync(query, cancellation);
    public Task<MarketData> FetchAsync(string symbol, bool includeIntraday, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.FetchAsync(symbol, cancellation) : yahoo.FetchAsync(symbol, includeIntraday, cancellation);
}
