using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Pages.Providers;

/// <summary>Admins register provider systems (name, contact, public keys) and switch them off.</summary>
public class IndexModel(PriorAuthDb db, PlanClock clock, TimeProvider time) : PageModel
{
    public IReadOnlyList<ProviderClient> Clients { get; private set; } = [];
    [TempData] public string? Notice { get; set; }
    public string? Problem { get; private set; }

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? ContactEmail { get; set; }
    [BindProperty] public string Jwks { get; set; } = "";

    public PlanClock Clock => clock;

    public async Task OnGetAsync() => await Load();

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        Problem = Check(out var keys);
        if (Problem is not null)
        {
            await Load();
            return Page();
        }
        var client = ProviderClient.Register(Name, keys!, time.GetUtcNow().UtcDateTime, ContactEmail);
        db.ProviderClients.Add(client);
        await db.SaveChangesAsync();
        Notice = $"Registered {client.Name}. Give the provider this client ID: {client.ClientId}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(string clientId)
    {
        var client = await db.ProviderClients.FindAsync(clientId);
        if (client is null) return NotFound();
        client.Deactivate();
        await db.SaveChangesAsync();
        Notice = $"{client.Name} can no longer sign in.";
        return RedirectToPage();
    }

    private string? Check(out string? keys)
    {
        keys = null;
        if (string.IsNullOrWhiteSpace(Name)) return "Enter the provider's name.";
        var email = ContactEmail?.Trim() ?? "";
        if (email.Length > 0 && !ProviderClient.LooksLikeEmail(email)) return "The contact email is not an email address.";
        try
        {
            using var document = JsonDocument.Parse(Jwks ?? "");
            var problem = ClientKeys.CheckKeySet(document.RootElement);
            if (problem is not null) return problem;
            keys = document.RootElement.GetRawText();
            return null;
        }
        catch (JsonException)
        {
            return "The public keys are not JSON. Paste the provider's JSON Web Key Set, which starts with {\"keys\": [";
        }
    }

    private async Task Load() => Clients = await db.ProviderClients.AsNoTracking().OrderByDescending(c => c.Active).ThenBy(c => c.Name).ToListAsync();
}
