using Microsoft.AspNetCore.Mvc.RazorPages;
using PriorAuth.Api.Auth;

namespace PriorAuth.Api.Pages.SignIn;

/// <summary>The Development stand-in for Windows sign-in: pick a test user. The form posts to the endpoint in <see cref="StaffSignIn"/>.</summary>
public class DevSignInModel(IConfiguration config, IWebHostEnvironment environment) : PageModel
{
    public IReadOnlyList<TestStaffUser> Users { get; private set; } = [];
    public string ReturnUrl { get; private set; } = "/";

    public Microsoft.AspNetCore.Mvc.IActionResult OnGet(string? returnUrl)
    {
        if (StaffSignIn.Mode(config, environment) != StaffSignInMode.Development) return NotFound();
        Users = StaffSignIn.TestUsers(config);
        ReturnUrl = returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return Page();
    }
}
