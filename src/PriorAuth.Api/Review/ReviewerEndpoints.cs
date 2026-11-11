using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Review;

public record ApproveBody(DateOnly Through, string Reviewer);
public record DenyBody(string Reason, string Reviewer);
public record RequestInformationBody(string Missing, string Reviewer);
public record DocumentSummary(string Id, string Title, string ContentType, int SizeBytes, DateTime ReceivedAtUtc);

/// <summary>
/// The endpoints the agency's reviewer screen calls. These are internal and are not part of the FHIR API.
/// NOTE: there is no staff sign-in yet, so the reviewer's name is taken from the request body.
/// </summary>
public static class ReviewerEndpoints
{
    public static void MapReviewerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/requests").LocalCallersOnly();

        group.MapGet("", async (PriorAuthDb db) =>
        {
            var requests = await db.AuthorizationRequests.Include(r => r.History).OrderByDescending(r => r.ReceivedAtUtc).ToListAsync();
            // Document details without the file contents.
            var documents = (await db.SupportingDocuments
                    .Select(d => new { RequestId = EF.Property<string>(d, "AuthorizationRequestId"), d.Id, d.Title, d.ContentType, d.SizeBytes, d.ReceivedAtUtc })
                    .ToListAsync())
                .ToLookup(d => d.RequestId, d => new DocumentSummary(d.Id, d.Title, d.ContentType, d.SizeBytes, d.ReceivedAtUtc));
            return Results.Json(requests.Select(r => Summary(r, documents[r.Id])));
        });

        group.MapGet("{id}/documents/{documentId}", async (string id, string documentId, PriorAuthDb db, HttpResponse response) =>
        {
            var document = await db.SupportingDocuments.FirstOrDefaultAsync(d =>
                d.Id == documentId && EF.Property<string>(d, "AuthorizationRequestId") == id);
            if (document is null) return Results.NotFound();

            // The file came from outside: always download it, and never let the browser guess its type.
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(document.Data, document.ContentType, fileDownloadName: SafeFileName(document.Title));
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
        return Results.Json(Summary(request, []));
    }

    private static string NewAuthorizationNumber(TimeProvider clock) =>
        $"PA-{clock.GetUtcNow():yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private static string SafeFileName(string title)
    {
        var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim();
        return cleaned.Length == 0 ? "document" : cleaned[..Math.Min(cleaned.Length, 80)];
    }

    private static object Summary(AuthorizationRequest r, IEnumerable<DocumentSummary> documents) => new
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
        Documents = documents.OrderBy(d => d.ReceivedAtUtc),
        History = r.History.OrderBy(h => h.AtUtc).ThenBy(h => h.Id).Select(h => new { h.AtUtc, h.Actor, h.Text }),
    };
}
