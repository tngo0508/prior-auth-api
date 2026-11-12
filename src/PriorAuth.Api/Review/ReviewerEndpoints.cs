using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Review;

public record ApproveBody(DateOnly Through, string Reviewer);
public record DenyBody(string Reason, string Reviewer);
public record RequestInformationBody(string Missing, string Reviewer);
public record DocumentSummary(string Id, string Title, string ContentType, int SizeBytes, DateTime ReceivedAtUtc);
public record HistorySummary(DateTime AtUtc, string Actor, string Text);

/// <summary>
/// The endpoints the agency's reviewer screen calls. These are internal and are not part of the FHIR API.
/// NOTE: there is no staff sign-in yet, so the reviewer's name is taken from the request body.
/// </summary>
public static class ReviewerEndpoints
{
    public const int MaxPageSize = 200;

    public static void MapReviewerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/requests").LocalCallersOnly();

        // The queue. status is open (default), decided or all. Never loads document contents or stored Bundles.
        group.MapGet("", async (string? status, int? skip, int? take, PriorAuthDb db) =>
        {
            var query = db.AuthorizationRequests.AsNoTracking();
            query = (status ?? "open").ToLowerInvariant() switch
            {
                "open" => query.Where(r => r.Status == AuthorizationStatus.Pended || r.Status == AuthorizationStatus.InformationRequested),
                "decided" => query.Where(r => r.Status != AuthorizationStatus.Pended && r.Status != AuthorizationStatus.InformationRequested),
                _ => query,
            };
            var page = await query.OrderByDescending(r => r.ReceivedAtUtc)
                .Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, MaxPageSize))
                .Select(r => new
                {
                    r.Id, r.ProviderRequestId, r.ProviderName, r.MemberName, r.MemberIdentifier, r.ServiceCode, r.ServiceName,
                    r.ReviewType, r.Status, r.AuthorizationNumber, r.AuthorizedThrough, r.Reason, r.ReceivedAtUtc, r.DecidedAtUtc, r.DecidedBy, r.Version,
                })
                .ToListAsync();

            var ids = page.Select(r => r.Id).ToList();
            var documents = (await db.SupportingDocuments.AsNoTracking()
                    .Where(d => ids.Contains(EF.Property<string>(d, "AuthorizationRequestId")))
                    .Select(d => new { RequestId = EF.Property<string>(d, "AuthorizationRequestId"), d.Id, d.Title, d.ContentType, d.SizeBytes, d.ReceivedAtUtc })
                    .ToListAsync())
                .ToLookup(d => d.RequestId, d => new DocumentSummary(d.Id, d.Title, d.ContentType, d.SizeBytes, d.ReceivedAtUtc));
            var history = (await db.HistoryEntries.AsNoTracking()
                    .Where(h => ids.Contains(EF.Property<string>(h, "AuthorizationRequestId")))
                    .Select(h => new { RequestId = EF.Property<string>(h, "AuthorizationRequestId"), h.Id, h.AtUtc, h.Actor, h.Text })
                    .ToListAsync())
                .OrderBy(h => h.AtUtc).ThenBy(h => h.Id)
                .ToLookup(h => h.RequestId, h => new HistorySummary(h.AtUtc, h.Actor, h.Text));

            return Results.Json(page.Select(r => new
            {
                r.Id, r.ProviderRequestId, r.ProviderName, r.MemberName, r.MemberIdentifier, r.ServiceCode, r.ServiceName,
                ReviewType = r.ReviewType?.ToString(),
                Status = r.Status.ToString(),
                IsOpen = r.Status is AuthorizationStatus.Pended or AuthorizationStatus.InformationRequested,
                r.AuthorizationNumber, r.AuthorizedThrough, r.Reason, r.ReceivedAtUtc, r.DecidedAtUtc, r.DecidedBy, r.Version,
                Documents = documents[r.Id].OrderBy(d => d.ReceivedAtUtc),
                History = history[r.Id],
            }));
        });

        group.MapGet("{id}/documents/{documentId}", async (string id, string documentId, PriorAuthDb db, HttpResponse response) =>
        {
            var document = await db.SupportingDocuments.AsNoTracking().FirstOrDefaultAsync(d =>
                d.Id == documentId && EF.Property<string>(d, "AuthorizationRequestId") == id);
            if (document is null) return Results.NotFound();

            // The file came from outside: always download it, and never let the browser guess its type.
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(document.Data, document.ContentType, fileDownloadName: SafeFileName(document.Title));
        });

        group.MapPost("{id}/approve", (string id, ApproveBody body, PriorAuthDb db, PlanClock clock) =>
            Decide(id, db, r => r.Approve(body.Through, NewAuthorizationNumber(clock), body.Reviewer, clock.UtcNow, clock.Today)));

        group.MapPost("{id}/deny", (string id, DenyBody body, PriorAuthDb db, PlanClock clock) =>
            Decide(id, db, r => r.Deny(body.Reason, body.Reviewer, clock.UtcNow)));

        group.MapPost("{id}/request-information", (string id, RequestInformationBody body, PriorAuthDb db, PlanClock clock) =>
            Decide(id, db, r => r.RequestInformation(body.Missing, body.Reviewer, clock.UtcNow)));
    }

    private static async Task<IResult> Decide(string id, PriorAuthDb db, Action<AuthorizationRequest> action)
    {
        var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (request is null) return Results.NotFound();
        try
        {
            action(request);
            await db.SaveChangesAsync();
        }
        catch (DomainRuleException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed the request after it was loaded: another reviewer, or the provider answering an information request.
            return Results.Problem("This request was changed by someone else a moment ago. Reload it and check before deciding.",
                statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Json(new { request.Id, Status = request.Status.ToString(), request.AuthorizationNumber, request.AuthorizedThrough, request.Reason, request.DecidedBy, request.Version });
    }

    /// <summary>Numbers carry the plan's local date of the decision.</summary>
    private static string NewAuthorizationNumber(PlanClock clock) =>
        $"PA-{clock.Today:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private static string SafeFileName(string title)
    {
        var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim();
        return cleaned.Length == 0 ? "document" : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}
