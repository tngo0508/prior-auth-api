using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Review;

public record ApproveBody(DateOnly Through);
public record ApproveWithChangesBody(DateOnly Through, string Change);
public record DenyBody(string ReasonCode, string Reason);
public record RequestInformationBody(string Missing);
public record AssignBody(string Account);
public record DocumentSummary(string Id, string Title, string ContentType, int SizeBytes, DateTime ReceivedAtUtc);
public record HistorySummary(DateTime AtUtc, string Actor, string Text);

/// <summary>
/// The review queue as JSON, for tools and tests. The portal pages do the same through <see cref="ReviewDesk"/>.
/// Internal, not part of the FHIR API. Every call needs a signed-in coordinator, licensed clinician or physician;
/// who did what is taken from the sign-in.
/// </summary>
public static class ReviewerEndpoints
{
    public const int MaxPageSize = 200;

    public static void MapReviewerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/requests").StaffOnly(StaffSignIn.QueuePolicy);

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
                    r.DecidedByAccount, r.Modification, r.DenialReasonCode, r.AssignedTo, r.AssignedToName,
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
                r.DecidedByAccount, r.Modification, r.DenialReasonCode, r.AssignedTo, r.AssignedToName,
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

        group.MapPost("{id}/claim", (string id, HttpContext http, ReviewDesk desk) => Answer(desk.Claim(id, Me(http))));
        group.MapPost("{id}/release", (string id, HttpContext http, ReviewDesk desk) => Answer(desk.Release(id, Me(http))));
        group.MapPost("{id}/assign", (string id, AssignBody body, HttpContext http, ReviewDesk desk) => Answer(desk.Assign(id, body.Account, Me(http))));
        group.MapPost("{id}/approve", (string id, ApproveBody body, HttpContext http, ReviewDesk desk) => Answer(desk.Approve(id, body.Through, Me(http))));
        group.MapPost("{id}/approve-with-changes", (string id, ApproveWithChangesBody body, HttpContext http, ReviewDesk desk) =>
            Answer(desk.ApproveWithChanges(id, body.Through, body.Change, Me(http))));
        group.MapPost("{id}/deny", (string id, DenyBody body, HttpContext http, ReviewDesk desk) => Answer(desk.Deny(id, body.ReasonCode, body.Reason, Me(http))));
        group.MapPost("{id}/request-information", (string id, RequestInformationBody body, HttpContext http, ReviewDesk desk) =>
            Answer(desk.RequestInformation(id, body.Missing, Me(http))));
    }

    // The policy has already required a signed-in staff member.
    private static Staff Me(HttpContext http) => http.User.CurrentStaff()!;

    private static async Task<IResult> Answer(Task<DeskResult> action)
    {
        var result = await action;
        var request = result.Request;
        return result.Outcome switch
        {
            DeskOutcome.NotFound => Results.NotFound(),
            DeskOutcome.Refused => Results.Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
            DeskOutcome.Conflict => Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(new
            {
                request!.Id, Status = request.Status.ToString(), request.AuthorizationNumber, request.AuthorizedThrough, request.Reason,
                request.Modification, request.DenialReasonCode, request.DecidedBy, request.DecidedByAccount,
                request.AssignedTo, request.AssignedToName, request.Version,
            }),
        };
    }

    private static string SafeFileName(string title)
    {
        var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim();
        return cleaned.Length == 0 ? "document" : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}
