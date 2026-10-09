using System.Text.Json;
namespace MarketLens.Services;

// A local JSON file avoids external database packages for this first lesson.
public sealed class WatchlistStore(IWebHostEnvironment environment)
{
    private readonly string path = Path.Combine(environment.ContentRootPath, "App_Data", "watchlist.json");
    private readonly object gate = new();
    public HashSet<string> Read() { lock (gate) return ReadCore(); }
    private HashSet<string> ReadCore() => File.Exists(path)
        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? [] : [];
    public void Toggle(string symbol)
    {
        lock (gate)
        {
            var symbols = ReadCore();
            if (!symbols.Remove(symbol)) symbols.Add(symbol);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(symbols));
            File.Move(temp, path, overwrite: true);
        }
    }
}
