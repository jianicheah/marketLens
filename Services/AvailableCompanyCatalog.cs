using MarketLens.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MarketLens.Services;

public sealed record AvailableCountry(string Code, string Name, int Count);

// Contains successful price responses only, never directory suggestions or imported/demo data.
public sealed class AvailableCompanyCatalog(TimeProvider clock) : IDisposable
{
    private readonly MemoryCache prices = new(new MemoryCacheOptions { SizeLimit = 128 });
    private readonly Dictionary<string, (string Source, CompanySearchResult Company)> entries = new();
    private readonly object gate = new();
    public void Record(string source, MarketData data)
    {
        DataValidation.Validate(data, clock.GetUtcNow());
        if (data.IsSample || data.IsImported || data.Daily.Length == 0) return;
        var key = source + ":" + data.Symbol;
        var exchange = CountryName(CountryCode(data.Symbol));
        var company = new CompanySearchResult(data.Symbol, data.Symbol[(data.Symbol.IndexOf('.') + 1)..], data.Name, exchange, Currency: data.Currency);
        lock (gate)
        {
            Prune();
            if (!entries.ContainsKey(key) && entries.Count >= 128)
            {
                var oldest = entries.Keys.First();
                entries.Remove(oldest); prices.Remove(oldest);
            }
            prices.Set(key, data, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            entries[key] = (source, company);
        }
    }
    private void Prune()
    {
        foreach (var key in entries.Keys.Where(key => !prices.TryGetValue(key, out _)).ToArray()) entries.Remove(key);
    }
    public MarketData? Read(string source, string symbol) => prices.TryGetValue<MarketData>(source + ":" + symbol, out var data) ? data : null;
    public CompanySearchResult[] Companies(string source, string? country = null, string? query = null)
    {
        lock (gate)
        {
            Prune();
            return entries.Values.Where(x => x.Source == source && (country is null || CountryCode(x.Company.Symbol) == country))
                .Select(x => x.Company).Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || x.Symbol.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name).ToArray();
        }
    }
    public AvailableCountry[] Countries(string source) => Companies(source).GroupBy(x => CountryCode(x.Symbol))
        .Select(x => new AvailableCountry(x.Key, CountryName(x.Key), x.Count())).OrderBy(x => x.Name).ToArray();
    public static string CountryCode(string symbol) => symbol.Split('.')[0] is "SH" or "SZ" ? "CN" : symbol.Split('.')[0];
    public static string CountryName(string code) => code switch
    {
        "US" => "United States", "MY" => "Malaysia", "HK" => "Hong Kong", "SG" => "Singapore", "CN" => "China",
        "JP" => "Japan", "AU" => "Australia", "CA" => "Canada", "UK" => "United Kingdom", "DE" => "Germany",
        "FR" => "France", "NL" => "Netherlands", "IT" => "Italy", "CH" => "Switzerland", "ES" => "Spain",
        "IN" => "India", "KR" => "South Korea", "TW" => "Taiwan", "ID" => "Indonesia", "TH" => "Thailand",
        "NZ" => "New Zealand", "BR" => "Brazil", "MX" => "Mexico", "ZA" => "South Africa", _ => code
    };
    public void Dispose() => prices.Dispose();
}
