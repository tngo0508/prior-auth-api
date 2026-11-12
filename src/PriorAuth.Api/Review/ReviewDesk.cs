using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Review;

/// <summary>A reason the plan may give for a denial, from configuration (DenialReasons).</summary>
public sealed class DenialReason
{
    public string Code { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>The plan's list of denial reasons. Checked at start-up: at least one, and no code twice.</summary>
public sealed class DenialReasons
{
    public DenialReasons(IConfiguration config)
    {
        All = config.GetSection("DenialReasons").Get<DenialReason[]>() ?? [];
        if (All.Count == 0) throw new InvalidOperationException("DenialReasons must list at least one reason.");
        foreach (var reason in All)
            if (string.IsNullOrWhiteSpace(reason.Code) || string.IsNullOrWhiteSpace(reason.Text))
                throw new InvalidOperationException("Each entry in DenialReasons needs a Code and a Text.");
        var repeated = All.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null) throw new InvalidOperationException($"DenialReasons lists the code '{repeated.Key}' more than once.");
    }

    public IReadOnlyList<DenialReason> All { get; }

    public DenialReason? Find(string? code) => All.FirstOrDefault(r => r.Code.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public enum DeskOutcome { Done, NotFound, Refused, Conflict }

/// <summary>What happened when a staff member acted on a request.</summary>
public sealed record DeskResult(DeskOutcome Outcome, AuthorizationRequest? Request = null, string? Message = null);

/// <summary>
/// Every action staff take on a request, for both the portal pages and the internal JSON endpoints:
/// load it, apply the domain rule, save, and turn rule breaks and lost races into a sentence.
/// </summary>
public sealed class ReviewDesk(PriorAuthDb db, PlanClock clock, DenialReasons reasons)
{
    public const string ConflictMessage = "This request was changed by someone else a moment ago. Reload it and check before acting.";

    public Task<DeskResult> Claim(string id, Staff by) => Act(id, r => r.Claim(by, clock.UtcNow));

    public Task<DeskResult> Release(string id, Staff by) => Act(id, r => r.Release(by, clock.UtcNow));

    public async Task<DeskResult> Assign(string id, string account, Staff by)
    {
        var target = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Account == account);
        if (target is null) return new(DeskOutcome.Refused, Message: "That person has not signed in to the portal yet, so they cannot be assigned requests.");
        return await Act(id, r => r.AssignTo(target.AsStaff(), by, clock.UtcNow));
    }

    public Task<DeskResult> Approve(string id, DateOnly through, Staff by) =>
        Act(id, r => r.Approve(through, NewAuthorizationNumber(), by, clock.UtcNow, clock.Today));

    public Task<DeskResult> ApproveWithChanges(string id, DateOnly through, string change, Staff by) =>
        Act(id, r => r.ApproveWithChanges(through, change, NewAuthorizationNumber(), by, clock.UtcNow, clock.Today));

    public Task<DeskResult> Deny(string id, string reasonCode, string reason, Staff by)
    {
        var listed = reasons.Find(reasonCode);
        return listed is null
            ? Task.FromResult(new DeskResult(DeskOutcome.Refused, Message: "Choose a denial reason from the plan's list."))
            : Act(id, r => r.Deny(listed.Code, reason, by, clock.UtcNow));
    }

    public Task<DeskResult> RequestInformation(string id, string missing, Staff by) =>
        Act(id, r => r.RequestInformation(missing, by, clock.UtcNow));

    private async Task<DeskResult> Act(string id, Action<AuthorizationRequest> action)
    {
        var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (request is null) return new(DeskOutcome.NotFound);
        try
        {
            action(request);
            await db.SaveChangesAsync();
            return new(DeskOutcome.Done, request);
        }
        catch (DomainRuleException ex)
        {
            return new(DeskOutcome.Refused, request, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed the request after it was loaded: another reviewer, or the provider answering an information request.
            return new(DeskOutcome.Conflict, request, ConflictMessage);
        }
    }

    /// <summary>Numbers carry the plan's local date of the decision.</summary>
    private string NewAuthorizationNumber() => $"PA-{clock.Today:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
}
