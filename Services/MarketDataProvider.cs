using MarketLens.Models;
namespace MarketLens.Services;

public sealed class MarketDataProvider(PublicMarketDataProvider yahoo, MarketstackDataProvider marketstack, IConfiguration configuration)
{
    public bool UsesMarketstack => string.Equals(configuration["MarketData:Provider"], "Marketstack", StringComparison.OrdinalIgnoreCase);
    public string Name => UsesMarketstack ? "Marketstack" : "Yahoo Finance";
    public Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.SearchAsync(query, cancellation) : yahoo.SearchAsync(query, cancellation);
    public Task<MarketData> FetchAsync(string symbol, bool includeIntraday, CancellationToken cancellation) => UsesMarketstack
        ? marketstack.FetchAsync(symbol, cancellation) : yahoo.FetchAsync(symbol, includeIntraday, cancellation);
}
