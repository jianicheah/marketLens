using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using MarketLens.Models;
namespace MarketLens.Services;

public sealed class DatasetStore(IWebHostEnvironment environment, TimeProvider clock, RenderHosting hosting)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string directory = Path.Combine(environment.ContentRootPath, "App_Data", "datasets");
    private readonly object gate = new();
    private readonly MemoryCache publicCache = new(new MemoryCacheOptions { SizeLimit = 32 });
    public bool IsPublic => hosting.IsPublic;
    private string PathFor(string symbol)
    {
        if (!DataValidation.ValidSymbol(symbol)) throw new InvalidDataException("Invalid symbol.");
        return Path.Combine(directory, symbol + ".json");
    }
    public MarketData? Read(string symbol)
    {
        lock (gate)
        {
            var path = PathFor(symbol);
            if (IsPublic) return publicCache.TryGetValue<MarketData>(symbol, out var cached) ? cached : null;
            return File.Exists(path) ? JsonSerializer.Deserialize<MarketData>(File.ReadAllText(path), JsonOptions) : null;
        }
    }
    public string[] Symbols()
    {
        if (IsPublic) return []; // Never pick another visitor's last-used company.
        lock (gate) return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(DataValidation.ValidSymbol).ToArray() : [];
    }
    public void Save(MarketData data)
    {
        DataValidation.Validate(data, clock.GetUtcNow());
        if (IsPublic)
        {
            if (data.IsImported || data.IsSample) throw new InvalidDataException("Only public provider data can enter the shared cache.");
            publicCache.Set(data.Symbol, data, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });
            return;
        }
        lock (gate)
        {
            Directory.CreateDirectory(directory); var path = PathFor(data.Symbol);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, JsonOptions));
            File.Move(path + ".tmp", path, true);
        }
    }
}
