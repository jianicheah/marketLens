using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MarketLens.Models;
using MarketLens.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace MarketLens.Pages;

public class IndexModel(DatasetStore store, MarketDataProvider marketData, AnalysisService analysis,
    BacktestService backtest, AiExplanationService ai, TimeProvider clock) : PageModel
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
    public BacktestResult? Backtest { get; private set; }
    public AiExplanation? Ai { get; private set; }
    public bool DailyOnly => marketData.UsesMarketstack;
    public bool AiEnabled => ai.Enabled;
    public string[] Symbols { get; private set; } = [];
    public CompanySearchResult[] Companies { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public bool AutoFetch { get; set; }
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        Symbol = Symbol?.Trim().ToUpperInvariant();
        if (!Demo && (store.IsPublic || AutoFetch) && DataValidation.ValidSymbol(Symbol))
        {
            try { store.Save(await marketData.FetchAsync(Symbol!, Horizon == "day", cancellation)); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
            { Error = e is InvalidDataException ? e.Message : "Market data is unavailable. Try Fetch latest data again shortly."; }
        }
        OnGet();
    }
    private void OnGet()
    {
        if (Horizon is not ("day" or "swing" or "long")) Horizon = "swing";
        if (CostBps is < 0 or > 100) { Error = "Costs must be 0–100 basis points per side."; CostBps = 10; }
        try
        {

            Symbols = store.Symbols().Where(s => string.IsNullOrWhiteSpace(Query) || s.Contains(Query, StringComparison.OrdinalIgnoreCase)).Order().ToArray();
            if (Demo) Data = DemoData.Create(clock.GetUtcNow());
            else
            {
                Symbol ??= Symbols.FirstOrDefault();
                if (Symbol is not null && DataValidation.ValidSymbol(Symbol)) Data = store.Read(Symbol);
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
    {
        OnGet();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            try { Companies = await marketData.SearchAsync(Query, cancellation); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
            { Error = e is InvalidDataException ? e.Message : "Company search is unavailable. You can still enter a known symbol or use saved data."; }
        }
        return Page();
    }
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellation)
    {
        Symbol = Symbol?.Trim().ToUpperInvariant();
        if (!DataValidation.ValidSymbol(Symbol)) { Notice = "Enter a valid symbol such as US.AAPL or MY.1155."; return RedirectToPage(); }
        try
        {
            var data = await marketData.FetchAsync(Symbol!, Horizon == "day", cancellation);
            store.Save(data); Notice = $"Fetched {data.Daily.Length} daily and {data.Intraday.Length} intraday bars for {data.Symbol}.";
        }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
        { Notice = e is InvalidDataException ? e.Message : "The public data request failed. Check the symbol or try again later. No account login is required; saved data is unchanged."; }
        return RedirectToPage(new { Symbol, Horizon, OwnsStock, CostBps });
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
    public static string DisplaySource(string source) => source == "Yahoo Finance public data (unofficial)" ? "Market data via Yahoo Finance" : source;
    public static string DisplayTime(DateTimeOffset? time) => time?.ToOffset(TimeSpan.FromHours(8)).ToString("dd MMM yyyy HH:mm 'SGT'") ?? "Unavailable";
}
