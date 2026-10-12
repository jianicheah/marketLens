using System.Text.Json;
using MarketLens.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace MarketLens.Pages;

[RequestSizeLimit(4 * 1024 * 1024)]
public class DataModel(ImportService import, DatasetStore store, MarketDataProvider provider) : PageModel
{
    public bool UsesMarketstack => provider.UsesMarketstack;
    public bool IsPublic => store.IsPublic;
    public bool SourceConfigured => provider.Configured;
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    public CompanySearchResult[] Candidates { get; private set; } = [];
    public async Task<IActionResult> OnGetSearchAsync(CancellationToken cancellation)
    {
        if (!SourceConfigured) { Notice = "Configure the provider connection before searching."; return Page(); }
        if (string.IsNullOrWhiteSpace(Query) || Query.Length > 80) { Notice = "Enter a company name or ticker, up to 80 characters."; return Page(); }
        try { Candidates = await provider.SearchAsync(Query, cancellation); }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
        { Notice = e is InvalidDataException ? e.Message : "Provider search is unavailable. Try again later."; }
        return Page();
    }
    public async Task<IActionResult> OnPostVerifyAsync(string? listingSymbol, CancellationToken cancellation)
    {
        if (!DataValidation.ValidSymbol(listingSymbol)) { Notice = "Choose a company from the search results."; return RedirectToPage(); }
        try
        {
            var data = await provider.FetchAsync(listingSymbol!, false, cancellation);
            store.Save(data);
            Notice = $"Daily data is available for {data.Name}. It now appears under {AvailableCompanyCatalog.CountryName(AvailableCompanyCatalog.CountryCode(data.Symbol))}.";
            return RedirectToPage("/Index", new { Symbol = data.Symbol, Country = AvailableCompanyCatalog.CountryCode(data.Symbol) });
        }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
        { Notice = e is InvalidDataException ? e.Message : "Daily data could not be retrieved. This company was not added to the available catalogue."; return RedirectToPage(); }
    }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string Symbol { get; set; } = "US.AAPL";
    [BindProperty] public string CompanyName { get; set; } = "Apple";
    [BindProperty] public bool SplitAdjusted { get; set; }
    [TempData] public string? Notice { get; set; }
    public async Task<IActionResult> OnPostImportAsync()
    {
        if (IsPublic) return StatusCode(403);
        if (Upload is null || Upload.Length is <= 0 or > 3 * 1024 * 1024) { Notice = "Choose a CSV or JSON file under 3 MB."; return RedirectToPage(); }
        try
        {
            using var reader = new StreamReader(Upload.OpenReadStream());
            var data = import.Parse(await reader.ReadToEndAsync(), Path.GetExtension(Upload.FileName), Symbol.Trim().ToUpperInvariant(), CompanyName.Trim(), SplitAdjusted);
            store.Save(data); return RedirectToPage("/Index", new { data.Symbol });
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { Notice = e is InvalidDataException ? e.Message : "File schema, number format, or timestamp is invalid. The prior dataset was preserved."; return RedirectToPage(); }
    }
}
