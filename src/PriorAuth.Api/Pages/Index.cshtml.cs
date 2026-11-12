using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Pages;

/// <summary>The review queue. Admins, who do not work the queue, are sent to the provider systems page.</summary>
public class IndexModel(PriorAuthDb db, PlanClock clock, ServiceRequirementCatalog catalog) : PageModel
{
    public const int PageSize = 100;

    public record Row(string Id, DateTime ReceivedLocal, string MemberName, string ServiceCode, string ServiceName, string Program,
        ReviewType? ReviewType, AuthorizationStatus Status, string? AssignedToName, bool Mine);

    [BindProperty(SupportsGet = true)]
    public string Show { get; set; } = "waiting";

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public int WaitingCount { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var me = User.CurrentStaff()!;
        if (!me.WorksQueue) return RedirectToPage("/Providers/Index");

        var query = db.AuthorizationRequests.AsNoTracking();
        var open = query.Where(r => r.Status == AuthorizationStatus.Pended || r.Status == AuthorizationStatus.InformationRequested);
        WaitingCount = await open.CountAsync();
        query = Show switch
        {
            "mine" => open.Where(r => r.AssignedTo == me.Account),
            "decided" => query.Where(r => r.Status != AuthorizationStatus.Pended && r.Status != AuthorizationStatus.InformationRequested),
            "all" => query,
            _ => open,
        };
        if (Show is not ("mine" or "decided" or "all")) Show = "waiting";

        // Waiting requests: oldest first, so nothing sits at the bottom. Decided ones: newest first.
        query = Show is "waiting" or "mine" ? query.OrderBy(r => r.ReceivedAtUtc) : query.OrderByDescending(r => r.ReceivedAtUtc);
        var page = await query.Take(PageSize)
            .Select(r => new { r.Id, r.ReceivedAtUtc, r.MemberName, r.ServiceSystem, r.ServiceCode, r.ServiceName, r.ReviewType, r.Status, r.AssignedTo, r.AssignedToName })
            .ToListAsync();
        Rows = page.Select(r => new Row(r.Id, clock.LocalTime(r.ReceivedAtUtc), r.MemberName, r.ServiceCode, r.ServiceName,
            ProgramOf(catalog, r.ServiceSystem, r.ServiceCode), r.ReviewType, r.Status, r.AssignedToName, r.AssignedTo == me.Account)).ToList();
        return Page();
    }

    /// <summary>The program a service code belongs to on the plan's list; both when the code is shared (H0019).</summary>
    public static string ProgramOf(ServiceRequirementCatalog catalog, string system, string code)
    {
        var programs = catalog.All.Where(s => s.System == system && s.Code.Trim().Equals(code.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Program).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Order().ToList();
        return programs.Count == 0 ? "Not on the list" : string.Join(" or ", programs);
    }

    public static string Describe(ReviewType? review) => review switch
    {
        Domain.ReviewType.PriorAuthorization => "Prior authorization",
        Domain.ReviewType.ConcurrentReview => "Concurrent review",
        _ => "None needed",
    };

    public static string Describe(AuthorizationStatus status) => status switch
    {
        AuthorizationStatus.Pended => "Waiting for a decision",
        AuthorizationStatus.InformationRequested => "Waiting for the provider",
        AuthorizationStatus.NotRequired => "Not required",
        _ => status.ToString(),
    };
}
