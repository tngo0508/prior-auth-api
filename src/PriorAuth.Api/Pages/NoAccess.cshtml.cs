using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Pages;

/// <summary>
/// Shown to someone signed in without the role a page needs. Open to all, so it reads the staff sign-in itself:
/// a person with no role at all must still see who they are signed in as.
/// </summary>
public class NoAccessModel(IConfiguration config, IWebHostEnvironment environment) : PageModel
{
    public Staff? Me { get; private set; }
    public bool TestUsers { get; private set; }

    /// <summary>Each role and the Active Directory group that gives it, or null when the plan has not set one.</summary>
    public IReadOnlyList<(StaffRole Role, string? Group)> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var signedIn = await HttpContext.AuthenticateAsync(StaffSignIn.Scheme);
        if (signedIn.Succeeded)
        {
            HttpContext.User = signedIn.Principal; // so the page header shows them too
            Me = signedIn.Principal.CurrentStaff();
        }
        TestUsers = StaffSignIn.Mode(config, environment) == StaffSignInMode.Development;
        Groups = Enum.GetValues<StaffRole>()
            .Select(role => (role, config[$"StaffSignIn:Groups:{role}"] is { Length: > 0 } group && !string.IsNullOrWhiteSpace(group) ? group.Trim() : null))
            .ToList();
    }
}
