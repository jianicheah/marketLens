using MarketLens.Services;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var hosting = new RenderHosting(builder.Configuration);
if (hosting.IsPublic)
{
    var host = hosting.PublicHost;
    if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) != UriHostNameType.Dns)
        throw new InvalidOperationException("Public hosting requires RENDER_EXTERNAL_HOSTNAME, which Render supplies automatically.");
    builder.Configuration["AllowedHosts"] = host;
    builder.Configuration["LocalAi:Model"] = "";
    var port = builder.Configuration["PORT"] ?? "10000";
    if (!int.TryParse(port, out var number) || number is < 1 or > 65535)
        throw new InvalidOperationException("PORT must be a valid TCP port.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{number}");
}
builder.Services.AddSingleton(hosting);
builder.Services.AddRazorPages();
var protection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<DatasetStore>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<BacktestService>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 128);
builder.Services.AddSingleton<PublicMarketDataProvider>();
builder.Services.AddSingleton<AiExplanationService>();
builder.Services.AddHttpClient("PublicMarketData", client =>
{
    client.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MarketLens/1.0 personal-research");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("LocalAi", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:11434/");
    client.Timeout = TimeSpan.FromSeconds(45);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        hosting.IsPublic && context.Request.Path != "/health"
            ? RateLimitPartition.GetFixedWindowLimiter("public", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
            : RateLimitPartition.GetNoLimiter("local"));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    // Render health probes are allowed without exposing data or mutations.
    if (!(hosting.IsPublic && context.Request.Path == "/health") &&
        !hosting.Allows(context.Request.Host.Host, context.Connection.RemoteIpAddress))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; form-action 'self'";
    // Render terminates HTTPS at its edge; do not redirect internal HTTP and cause a proxy loop.
    if (hosting.IsPublic) context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});
app.UseExceptionHandler("/Error");
if (!app.Environment.IsDevelopment() && !hosting.IsPublic) app.UseHsts();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health", () => Results.Text("OK"));
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.Run();
