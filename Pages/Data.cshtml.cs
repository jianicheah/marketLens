using System.Text.Json;
using MarketLens.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace MarketLens.Pages;

[RequestSizeLimit(4 * 1024 * 1024)]
public class DataModel(ImportService import, DatasetStore store) : PageModel
{
    public bool IsPublic => store.IsPublic;
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
