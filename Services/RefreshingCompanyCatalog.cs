using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MarketLens.Services;

public sealed record CatalogueManifest(string Sha256, int Count, DateTimeOffset CheckedAt);
public sealed record CatalogueState(CompanySearchResult[] Companies, CatalogueManifest? Manifest);
public sealed record StoredCatalogue(CatalogueManifest Manifest, CompanySearchResult[] Companies, string PayloadHash);

public sealed class RefreshingCompanyCatalog
{
    private readonly IHttpClientFactory clients;
    private readonly TimeProvider clock;
    private readonly ILogger<RefreshingCompanyCatalog> logger;
    private readonly string remote;
    private readonly string storageFile;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CatalogueState state;
    private bool refreshFailed;
    public CatalogueState Current => Volatile.Read(ref state);
    public bool RefreshFailed => Volatile.Read(ref refreshFailed);

    public RefreshingCompanyCatalog(IHttpClientFactory clients, IConfiguration config, IWebHostEnvironment environment,
        TimeProvider clock, ILogger<RefreshingCompanyCatalog> logger)
    {
        this.clients = clients; this.clock = clock; this.logger = logger;
        remote = (config["Catalogue:RemoteBaseUrl"] ?? "https://raw.githubusercontent.com/jianicheah/marketLens/catalogue-data/Catalogue").TrimEnd('/');
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Catalogue remote storage requires an HTTPS URL.");
        storageFile = Path.Combine(config["Catalogue:StoragePath"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "catalogue"), "last-valid.json");
        var seed = config["MarketData:CompanyCatalogPath"] ?? Path.Combine(environment.ContentRootPath, "Catalogue", "company-catalogue-twelvedata.json");
        state = new(WebsiteCompanyCatalog.Read(seed), null);
        try
        {
            if (File.Exists(storageFile))
            {
                var stored = JsonSerializer.Deserialize<StoredCatalogue>(File.ReadAllText(storageFile), DatasetStore.JsonOptions)
                    ?? throw new InvalidDataException("Empty catalogue cache.");
                if (stored.Manifest is null || stored.Companies is null || string.IsNullOrWhiteSpace(stored.PayloadHash))
                    throw new InvalidDataException("Incomplete catalogue cache.");
                var companies = WebsiteCompanyCatalog.Validate(stored.Companies);
                if (companies.Length != stored.Manifest.Count || Hash(JsonSerializer.SerializeToUtf8Bytes(stored.Companies)) != stored.PayloadHash)
                    throw new InvalidDataException("Catalogue cache checksum failed.");
                ValidateManifest(stored.Manifest);
                state = new(companies, stored.Manifest);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { refreshFailed = true; logger.LogWarning("Saved catalogue could not be loaded; using the bundled catalogue."); }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private void ValidateManifest(CatalogueManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64 || !manifest.Sha256.All(char.IsAsciiHexDigit) ||
            manifest.Count <= 0 || manifest.Count > 1000000 || manifest.CheckedAt.Year < 2000 || manifest.CheckedAt > clock.GetUtcNow().AddMinutes(5))
            throw new InvalidDataException("Invalid catalogue manifest.");
    }

    public async Task RefreshAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var client = clients.CreateClient("Catalogue");
            var manifest = await client.GetFromJsonAsync<CatalogueManifest>(remote + "/manifest.json", cancellation)
                ?? throw new InvalidDataException("No catalogue manifest returned.");
            ValidateManifest(manifest);
            var current = Current;
            if (manifest == current.Manifest) { Volatile.Write(ref refreshFailed, false); return; }
            if (current.Manifest is { } old && manifest.CheckedAt < old.CheckedAt)
                throw new InvalidDataException("Refusing an older catalogue version.");
            CompanySearchResult[] companies;
            if (manifest.Sha256 == current.Manifest?.Sha256)
            {
                companies = current.Companies;
                if (manifest.Count != companies.Length) throw new InvalidDataException("Catalogue count changed without a new checksum.");
            }
            else
            {
                var bytes = await client.GetByteArrayAsync(remote + "/company-catalogue-twelvedata.json", cancellation);
                if (!string.Equals(Hash(bytes), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Catalogue download checksum failed.");
                companies = WebsiteCompanyCatalog.Validate(JsonSerializer.Deserialize<CompanySearchResult[]>(bytes, DatasetStore.JsonOptions)
                    ?? throw new InvalidDataException("No catalogue listings returned."));
                if (companies.Length != manifest.Count || companies.Length < current.Companies.Length * 0.8)
                    throw new InvalidDataException("Catalogue is incomplete or unexpectedly smaller; keeping the last valid version.");
                var counts = companies.GroupBy(x => x.Symbol.Split('.')[0]).ToDictionary(x => x.Key, x => x.Count());
                if (current.Companies.GroupBy(x => x.Symbol.Split('.')[0]).Any(group => counts.GetValueOrDefault(group.Key) < group.Count() * 0.8))
                    throw new InvalidDataException("A market unexpectedly lost most of its listings; keeping the last valid catalogue.");
            }
            var stored = new StoredCatalogue(manifest, companies, Hash(JsonSerializer.SerializeToUtf8Bytes(companies)));
            Directory.CreateDirectory(Path.GetDirectoryName(storageFile)!);
            var temporary = storageFile + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, DatasetStore.JsonOptions), cancellation);
            File.Move(temporary, storageFile, true);
            Volatile.Write(ref state, new(companies, manifest));
            Volatile.Write(ref refreshFailed, false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            Volatile.Write(ref refreshFailed, true);
            logger.LogWarning("Catalogue refresh failed; retaining the last valid catalogue.");
            if (cancellation.IsCancellationRequested) throw;
        }
        finally { gate.Release(); }
    }
}

public sealed class CatalogueRefreshWorker(RefreshingCompanyCatalog catalogue, IConfiguration config, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start asynchronously so the bundled/saved list is immediately usable.
        await Task.Yield();
        if (!config.GetValue("Catalogue:AutoRefresh", true)) return;
        if (string.Equals(config["MarketData:Provider"]?.Trim(), "Marketstack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config["MarketData:Provider"]?.Trim(), "Yahoo", StringComparison.OrdinalIgnoreCase)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            await catalogue.RefreshAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(15), clock, stoppingToken);
        }
    }
}
