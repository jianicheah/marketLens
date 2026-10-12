using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using MarketLens.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MarketLens.Pages;

[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("owner-login")]
public sealed class OwnerModel(IConfiguration configuration, RenderHosting hosting) : PageModel
{
    [BindProperty, Required, StringLength(100)] public string Username { get; set; } = "owner";
    [BindProperty, Required, StringLength(256)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }
    public IActionResult OnGet() => !hosting.IsPublic || User.Identity?.IsAuthenticated == true
        ? RedirectToPage("/Data") : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!hosting.IsPublic) return RedirectToPage("/Data");
        var expectedUser = configuration["Hosting:OwnerUser"] ?? "owner";
        var expectedPassword = configuration["Hosting:OwnerPassword"] ?? "";
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password));
        if (!ModelState.IsValid || expectedPassword.Length < 12 || !OwnerAccess.Accepts(header, expectedUser, expectedPassword))
        {
            Password = ""; ModelState.Remove(nameof(Password));
            Error = "The username or password is incorrect.";
            return Page();
        }
        await HttpContext.SignInAsync("Owner", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, expectedUser)], "Owner")),
            new AuthenticationProperties { IsPersistent = false });
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : RedirectToPage("/Data");
    }
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync("Owner");
        return RedirectToPage("/Index");
    }
}
