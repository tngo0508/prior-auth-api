using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Review;

namespace PriorAuth.Api.Pages.Requests;

/// <summary>One request: what was asked, the paperwork, the history, and the actions the signed-in person may take.</summary>
public class DetailModel(PriorAuthDb db, PlanClock clock, ReviewDesk desk, DenialReasons reasons, ServiceRequirementCatalog catalog) : PageModel
{
    public AuthorizationRequest AuthorizationRequest { get; private set; } = null!;
    public Staff Me { get; private set; } = null!;
    public string Program { get; private set; } = "";
    public IReadOnlyList<Staff> Assignable { get; private set; } = [];
    public IReadOnlyList<DenialReason> DenialReasons => reasons.All;
    public PlanClock Clock => clock;
    public DateOnly Today => clock.Today;

    [TempData] public string? Notice { get; set; }
    public string? Problem { get; private set; }

    /// <summary>The version the page showed. An action on an older version is refused, so no one decides on what they did not see.</summary>
    [BindProperty] public int Version { get; set; }

    public async Task<IActionResult> OnGetAsync(string id) => await Load(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostClaimAsync(string id) => Run(id, () => desk.Claim(id, Me), "You are working on this request.");

    public Task<IActionResult> OnPostReleaseAsync(string id) => Run(id, () => desk.Release(id, Me), "Released. The request is back in the queue.");

    public Task<IActionResult> OnPostAssignAsync(string id, string account) =>
        Run(id, () => desk.Assign(id, account ?? "", Me), "Assigned.");

    public Task<IActionResult> OnPostApproveAsync(string id, DateOnly? through) => through is { } date
        ? Run(id, () => desk.Approve(id, date, Me), "Approved.")
        : Refuse(id, "Choose the date the approval ends.");

    public Task<IActionResult> OnPostApproveWithChangesAsync(string id, DateOnly? through, string? change) => through is { } date
        ? Run(id, () => desk.ApproveWithChanges(id, date, change ?? "", Me), "Approved with changes.")
        : Refuse(id, "Choose the date the approval ends.");

    public Task<IActionResult> OnPostDenyAsync(string id, string? reasonCode, string? reason) =>
        Run(id, () => desk.Deny(id, reasonCode ?? "", reason ?? "", Me), "Denied.");

    public Task<IActionResult> OnPostRequestInformationAsync(string id, string? missing) =>
        Run(id, () => desk.RequestInformation(id, missing ?? "", Me), "Information requested. The provider sees what is missing when it next asks.");

    private async Task<IActionResult> Run(string id, Func<Task<DeskResult>> action, string done)
    {
        Me = User.CurrentStaff()!;
        var current = await db.AuthorizationRequests.AsNoTracking().Where(r => r.Id == id).Select(r => (int?)r.Version).FirstOrDefaultAsync();
        if (current is null) return NotFound();
        if (current != Version) return await Refuse(id, ReviewDesk.ConflictMessage);

        var result = await action();
        if (result.Outcome == DeskOutcome.NotFound) return NotFound();
        if (result.Outcome != DeskOutcome.Done) return await Refuse(id, result.Message!);
        Notice = done;
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> Refuse(string id, string message)
    {
        // Show the request as it is now, with the reason the action did not happen.
        db.ChangeTracker.Clear();
        if (!await Load(id)) return NotFound();
        Problem = message;
        return Page();
    }

    private async Task<bool> Load(string id)
    {
        Me = User.CurrentStaff()!;
        var request = await db.AuthorizationRequests.AsNoTracking().AsSplitQuery()
            .Include(r => r.Documents).Include(r => r.History)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request is null) return false;
        AuthorizationRequest = request;
        Version = request.Version;
        Program = IndexModel.ProgramOf(catalog, request.ServiceSystem, request.ServiceCode);
        if (Me.Has(StaffRole.Coordinator))
            Assignable = (await db.StaffMembers.AsNoTracking().OrderBy(m => m.DisplayName).ToListAsync())
                .Select(m => m.AsStaff()).Where(s => s.WorksQueue).ToList();
        return true;
    }

    public string Local(DateTime utc) => clock.LocalTime(utc).ToString("d MMM yyyy, h:mm tt");

    public bool HeldByAnother => AuthorizationRequest.AssignedTo is not null && AuthorizationRequest.AssignedTo != Me.Account;
    public bool MayDecide => AuthorizationRequest.IsOpen && Me.MayDecide(AuthorizationRequest.ReviewType) && !HeldByAnother;
    public bool MayRequestInformation => AuthorizationRequest.Status == AuthorizationStatus.Pended && Me.WorksQueue && !HeldByAnother;
}
