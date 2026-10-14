using System.Text.Json;
namespace MarketLens.Services;

public sealed record RadarEntry(string Symbol, DateTimeOffset CheckedAt, StockSignal? Result, string? Error);

// Separate durable screening results from the small, expiring interactive price cache.
public sealed class RadarStore(IWebHostEnvironment environment, IConfiguration configuration, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly string path = configuration["MarketData:RadarPath"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "radar", "results.json");
    private Dictionary<string, RadarEntry>? entries;
    private Dictionary<string, RadarEntry> Load()
    {
        if (entries is not null) return entries;
        try { entries = File.Exists(path) ? (JsonSerializer.Deserialize<RadarEntry[]>(File.ReadAllText(path), DatasetStore.JsonOptions) ?? []).Where(x => DataValidation.ValidSymbol(x.Symbol)).DistinctBy(x => x.Symbol).ToDictionary(x => x.Symbol) : []; }
        catch (Exception e) when (e is IOException or JsonException) { entries = []; }
        return entries;
    }
    public RadarEntry[] Current()
    {
        lock (gate) return Load().Values.Where(x => x.CheckedAt <= clock.GetUtcNow() && clock.GetUtcNow() - x.CheckedAt < TimeSpan.FromHours(24)).ToArray();
    }
    public void Save(RadarEntry entry)
    {
        lock (gate)
        {
            Load()[entry.Symbol] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Load().Values.ToArray(), DatasetStore.JsonOptions));
            File.Move(path + ".tmp", path, true);
        }
    }
}

public sealed class RadarWorker(MarketDataProvider marketData, RadarStore store, AnalysisService analysis, TimeProvider clock, ILogger<RadarWorker> logger) : BackgroundService
{
    // Begin with liquid instruments, rather than alphabetically sorted obscure funds.
    private static readonly string[] Priority = ["US.AAPL","US.NVDA","US.VOO","US.MSFT","US.AMZN","US.GOOGL","US.META","US.TSLA","US.JPM","US.V","US.WMT","US.SPY","US.QQQ","US.AMD","US.INTC","US.NFLX","US.DIS","US.NKE","US.BA","US.PFE","US.XOM","US.CVX","US.KO","US.PEP","US.COST","US.ADBE","US.CRM","US.PYPL","US.UNH","US.TGT","MY.1155","MY.1295","MY.5099","SG.D05","SG.O39","SG.U11","HK.0700","HK.9988"];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure startup is never blocked by network requests.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (marketData.Configured)
                {
                    var current = store.Current().Where(x => x.Result is null || x.Result.Prices is { Length: >= 2 }).Select(x => x.Symbol).ToHashSet();
                    var directory = marketData.AvailableCompanies();
                    var known = directory.Select(x => x.Symbol).ToHashSet();
                    // At most 100 current daily checks, leaving quota for interactive use.
                    var candidates = Priority.Where(known.Contains).Concat(directory.Select(x => x.Symbol)).Distinct().Where(x => !current.Contains(x)).Take(Math.Max(0, 100 - current.Count)).ToArray();
                    foreach (var symbol in candidates)
                    {
                        try
                        {
                            var data = await marketData.FetchListedAsync(symbol, stoppingToken);
                            var result = SignalOverview.Build([data], analysis, clock, false).Single();
                            store.Save(new(symbol, clock.GetUtcNow(), result, null));
                        }
                        catch (InvalidDataException e)
                        {
                            // Global quota/connection failures are not evidence about a listing.
                            if (e.Message.Contains("budget", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("key", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)) break;
                            store.Save(new(symbol, clock.GetUtcNow(), null, e.Message));
                        }
                        catch (Exception e) when (e is HttpRequestException or JsonException) { break; }
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { logger.LogWarning("Radar refresh could not complete ({Type}). Existing results retained.", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
