using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Review;

public record ApproveBody(DateOnly Through, string Reviewer);
public record DenyBody(string Reason, string Reviewer);
public record RequestInformationBody(string Missing, string Reviewer);

/// <summary>
/// The endpoints the agency's reviewer screen calls. These are internal and are not part of the FHIR API.
/// NOTE: there is no staff sign-in yet, so the reviewer's name is taken from the request body.
/// </summary>
public static class ReviewerEndpoints
{
    public static void MapReviewerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/requests");

        group.MapGet("", async (PriorAuthDb db) =>
        {
            var requests = await db.AuthorizationRequests.Include(r => r.History).OrderByDescending(r => r.ReceivedAtUtc).ToListAsync();
            return Results.Json(requests.Select(Summary));
        });

        group.MapPost("{id}/approve", (string id, ApproveBody body, PriorAuthDb db, TimeProvider clock) =>
            Decide(id, db, r => r.Approve(body.Through, NewAuthorizationNumber(clock), body.Reviewer, clock.GetUtcNow().UtcDateTime)));

        group.MapPost("{id}/deny", (string id, DenyBody body, PriorAuthDb db, TimeProvider clock) =>
            Decide(id, db, r => r.Deny(body.Reason, body.Reviewer, clock.GetUtcNow().UtcDateTime)));

        group.MapPost("{id}/request-information", (string id, RequestInformationBody body, PriorAuthDb db, TimeProvider clock) =>
            Decide(id, db, r => r.RequestInformation(body.Missing, body.Reviewer, clock.GetUtcNow().UtcDateTime)));
    }

    private static async Task<IResult> Decide(string id, PriorAuthDb db, Action<AuthorizationRequest> action)
    {
        var request = await db.AuthorizationRequests.Include(r => r.History).FirstOrDefaultAsync(r => r.Id == id);
        if (request is null) return Results.NotFound();
        try
        {
            action(request);
        }
        catch (DomainRuleException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        await db.SaveChangesAsync();
        return Results.Json(Summary(request));
    }

    private static string NewAuthorizationNumber(TimeProvider clock) =>
        $"PA-{clock.GetUtcNow():yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private static object Summary(AuthorizationRequest r) => new
    {
        r.Id,
        r.ProviderRequestId,
        r.ProviderName,
        r.MemberName,
        r.ServiceCode,
        r.ServiceName,
        Status = r.Status.ToString(),
        r.IsOpen,
        r.AuthorizationNumber,
        r.AuthorizedThrough,
        r.Reason,
        r.ReceivedAtUtc,
        r.DecidedAtUtc,
        r.DecidedBy,
        History = r.History.OrderBy(h => h.AtUtc).ThenBy(h => h.Id).Select(h => new { h.AtUtc, h.Actor, h.Text }),
    };
}
