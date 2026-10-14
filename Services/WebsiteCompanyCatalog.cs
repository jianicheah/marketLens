using System.Text.Json;

namespace MarketLens.Services;

// A deployable directory snapshot, separate from expiring price caches.
public static class WebsiteCompanyCatalog
{
    public static CompanySearchResult[] Read(string path)
    {
        if (!File.Exists(path)) return [];
        var companies = JsonSerializer.Deserialize<CompanySearchResult[]>(File.ReadAllText(path), DatasetStore.JsonOptions)
            ?? throw new InvalidDataException("The saved company catalogue must be a JSON array.");
        return Validate(companies);
    }
    public static CompanySearchResult[] Validate(CompanySearchResult[] companies)
    {
        if (companies is null) throw new InvalidDataException("Missing catalogue listings.");
        if (companies.Any(x => x is null || !DataValidation.ValidSymbol(x.Symbol) ||
            string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 150 ||
            string.IsNullOrWhiteSpace(x.ProviderSymbol) ||
            MarketDirectory.Find(x.ExchangeMic) is not { } exchange ||
            !x.Symbol.StartsWith(exchange.Market + ".", StringComparison.Ordinal) ||
            x.Symbol != exchange.Market + "." + x.ProviderSymbol))
            throw new InvalidDataException("The saved company catalogue contains an invalid listing or exchange.");
        return companies.DistinctBy(x => x.Symbol).OrderBy(x => x.Name).ToArray();
    }
}
