using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MarketLens.Models;
using MarketLens.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace MarketLens.Pages;

public class IndexModel(DatasetStore store, MarketDataProvider marketData, AnalysisService analysis,
    BacktestService backtest, AiExplanationService ai, TimeProvider clock, TwelveDataQuoteService? quotes = null, RadarStore? radar = null) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Symbol { get; set; }
    [BindProperty(SupportsGet = true)] public string Horizon { get; set; } = "swing";
    [BindProperty(SupportsGet = true)] public bool OwnsStock { get; set; }
    [BindProperty(SupportsGet = true)] public bool Demo { get; set; }
    [BindProperty(SupportsGet = true), Range(0, 100)] public decimal CostBps { get; set; } = 10;
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    [TempData] public string? Notice { get; set; }
    public string? Error { get; set; }
    public MarketData? Data { get; private set; }
    public Recommendation? Result { get; private set; }
    public StockSignal[] SignalStocks { get; private set; } = [];
    public int SignalCount(string signal) => SignalStocks.Count(x => x.Signal == signal);
    public int UnavailableCount { get; private set; }
    public int UncheckedCount { get; private set; }
    public IActionResult OnGetRadar()
    {
        Response.Headers.CacheControl = "no-store";
        OnGet();
        return Page();
    }
    public BacktestResult? Backtest { get; private set; }
    public AiExplanation? Ai { get; private set; }
    public bool CanManage => !store.IsPublic || User.Identity?.IsAuthenticated == true;
    public bool DailyOnly => marketData.DailyOnly;
    public bool AiEnabled => ai.Enabled;
    public string[] Symbols { get; private set; } = [];
    public CompanySearchResult[] Companies { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Country { get; set; }
    [BindProperty(SupportsGet = true), Range(0, int.MaxValue)] public int BrowseOffset { get; set; }
    public bool SourceConfigured => marketData.Configured;
    public AvailableCountry[] Countries => marketData.AvailableCountries;
    public int MatchingCount { get; private set; }
    public int TotalCompanyCount { get; private set; }
    public DateTimeOffset? CatalogueCheckedAt => marketData.CatalogueCheckedAt;
    public bool CatalogueRefreshFailed => marketData.CatalogueRefreshFailed;
    public bool UsesYahoo => !marketData.UsesManagedProvider;
    public bool ShowingSavedFallback { get; private set; }
    public bool ShowIntradayQuote => !Demo && marketData.UsesTwelveData && Symbol is not null && marketData.AvailableCompanies().Any(x => x.Symbol == Symbol);
    public async Task<IActionResult> OnGetQuoteAsync(CancellationToken cancellation)
    {
        Response.Headers.CacheControl = "no-store";
        var listing = Symbol is null ? null : marketData.AvailableCompanies().FirstOrDefault(x => x.Symbol == Symbol.Trim().ToUpperInvariant());
        if (!marketData.UsesTwelveData || listing is null) return NotFound();
        if (!marketData.Configured || quotes is null) return new JsonResult(new StockQuoteResult(null, "The website's intraday data connection is not configured.", 300));
        return new JsonResult(await quotes.GetAsync(listing, cancellation));
    }
    public const int CompanyPageSize = 10;
    public int CompanyPage => BrowseOffset / CompanyPageSize + 1;
    public int CompanyPageCount => Math.Max(1, (MatchingCount + CompanyPageSize - 1) / CompanyPageSize);
    public bool HasNext { get; private set; }
    public string ResultsTitle { get; private set; } = "Available companies";
    public string ListingStatus(string symbol) => marketData.ReadAvailable(symbol) is { } data
        ? $"Daily prices ready · {data.Daily.Length} bars"
        : SourceConfigured ? "Price availability checked on opening" : "Data connection not configured";
    private void LoadCompanies()
    {
        // Old country links also open the complete website list.
        Country = null;
        if (BrowseOffset < 0) BrowseOffset = 0;
        if (Query?.Length > 80) { Error = "Search must be 80 characters or fewer."; Companies = []; MatchingCount = 0; return; }
        ResultsTitle = Country is null ? "Available companies" : AvailableCompanyCatalog.CountryName(Country) + " companies";
        var matches = marketData.AvailableCompanies(Country, Query);
        MatchingCount = matches.Length;
        BrowseOffset = Math.Min(BrowseOffset / CompanyPageSize, CompanyPageCount - 1) * CompanyPageSize;
        Companies = matches.Skip(BrowseOffset).Take(CompanyPageSize).ToArray();
        HasNext = (long)BrowseOffset + CompanyPageSize < MatchingCount;
    }
    [BindProperty(SupportsGet = true)] public bool AutoFetch { get; set; }
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        NormalizeHorizon();
        Symbol = Symbol?.Trim().ToUpperInvariant();
        if (!Demo && DataValidation.ValidSymbol(Symbol))
        {
            try { store.Save(await marketData.FetchListedAsync(Symbol!, cancellation)); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
            { Error = e is InvalidDataException ? e.Message : "Daily prices could not be loaded. Please try again shortly."; }
        }
        OnGet();
    }
    private void OnGet()
    {
        NormalizeHorizon();
        LoadCompanies();
        SignalStocks = SignalOverview.Build(marketData.CachedDatasets(), analysis, clock, OwnsStock);
        var listed = marketData.AvailableCompanies().Select(x => x.Symbol).ToHashSet();
        TotalCompanyCount = listed.Count;
        var saved = radar?.Current() ?? [];
        SignalStocks = SignalStocks.Concat(saved.Where(x => x.Result is not null).Select(x => x.Result!)).Where(x => listed.Contains(x.Symbol)).DistinctBy(x => x.Symbol).OrderBy(x => x.Signal == "Buy" ? 0 : x.Signal == "Sell" ? 1 : x.Signal == "Hold" ? 2 : 3).ThenBy(x => x.Name).ToArray();
        var analysed = SignalStocks.Select(x => x.Symbol).ToHashSet();
        UnavailableCount = marketData.UnavailableSymbols().Concat(saved.Where(x => x.Result is null).Select(x => x.Symbol)).Distinct().Count(x => listed.Contains(x) && !analysed.Contains(x));
        UncheckedCount = Math.Max(0, listed.Count - SignalStocks.Length - UnavailableCount);
        if (CostBps is < 0 or > 100) { Error = "Costs must be 0–100 basis points per side."; CostBps = 10; }
        try
        {

            Symbols = store.Symbols().Where(s => string.IsNullOrWhiteSpace(Query) || s.Contains(Query, StringComparison.OrdinalIgnoreCase)).Order().ToArray();
            if (Demo) Data = DemoData.Create(clock.GetUtcNow());
            else
            {
                Symbol ??= Symbols.FirstOrDefault();
                if (Symbol is not null && DataValidation.ValidSymbol(Symbol))
                {
                    Data = marketData.ReadAvailable(Symbol);
                    if (Data is null)
                    {
                        Data = store.Read(Symbol);
                        ShowingSavedFallback = Data is not null;
                    }
                }
                if (Symbol is not null && Data is null && Error is null) Error = "Daily prices could not be loaded for this company. Please try again shortly.";
            }
            if (Data is not null)
            {
                DataValidation.Validate(Data, clock.GetUtcNow());
                Result = analysis.Evaluate(Data, Horizon, OwnsStock);
                if (Horizon == "swing" && Data.SplitAdjusted)
                {
                    DataValidation.Validate(Data, clock.GetUtcNow());
                    Backtest = backtest.Run(DataValidation.CompletedDaily(Data, clock.GetUtcNow()), CostBps);
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException)
        { Error = "Stored data could not be analysed. Re-import a valid dataset or refresh the source."; Data = null; Result = null; }
    }
    public async Task<IActionResult> OnGetSearchAsync(CancellationToken cancellation)
    { BrowseOffset = 0; await OnGetAsync(cancellation); return Page(); }
    public async Task<IActionResult> OnGetBrowseAsync(CancellationToken cancellation)
    { await OnGetAsync(cancellation); return Page(); }
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellation)
    {
        NormalizeHorizon();
        Symbol = Symbol?.Trim().ToUpperInvariant();
        if (!DataValidation.ValidSymbol(Symbol)) { Notice = "Enter a valid symbol such as US.AAPL or MY.1155."; return RedirectToPage(); }
        try
        {
            var data = await marketData.FetchAsync(Symbol!, Horizon == "day", cancellation);
            store.Save(data); Notice = $"Fetched {data.Daily.Length} daily and {data.Intraday.Length} intraday bars for {data.Symbol}.";
        }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
        { Notice = e is InvalidDataException ? e.Message : "The public data request failed. Check the symbol or try again later. No account login is required; saved data is unchanged."; }
        return RedirectToPage(new { Symbol, Horizon, OwnsStock, CostBps, Country });
    }
    public async Task<IActionResult> OnPostAiAsync(CancellationToken cancellation)
    {
        OnGet();
        if (Result is null) return BadRequest();
        try { Ai = await ai.ExplainAsync(Result, cancellation); }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
        { Error = e is InvalidDataException ? e.Message : "Local AI is unavailable. The original rule-based result remains visible."; }
        return Page();
    }
    public IActionResult OnGetExport()
    {
        OnGet();
        if (Result is null || Data is null) return NotFound();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Data.Symbol, GeneratedAt = clock.GetUtcNow(), Horizon, OwnsStock, Result, Backtest }, DatasetStore.JsonOptions);
        return File(bytes, "application/json", "MarketLens-analysis.json");
    }
    private void NormalizeHorizon()
    {
        // Neither current connector supplies actionable intraday data.
        if (Horizon is not ("swing" or "long") || DailyOnly && Horizon != "swing")
        {
            Horizon = "swing";
            ModelState.Remove(nameof(Horizon));
        }
    }
    public static string DisplaySource(string source) => source == "Yahoo Finance public data (unofficial)" ? "Market data via Yahoo Finance" : source;
    public static string DisplayTime(DateTimeOffset? time) => time?.ToOffset(TimeSpan.FromHours(8)).ToString("dd MMM yyyy HH:mm 'SGT'") ?? "Unavailable";
}
