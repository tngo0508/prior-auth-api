using Hl7.Fhir.Model;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;
using ClaimsPrincipal = System.Security.Claims.ClaimsPrincipal;

namespace PriorAuth.Api.Fhir;

/// <summary>The endpoints a provider's EHR calls.</summary>
public static class FhirEndpoints
{
    /// <summary>
    /// The largest $submit body accepted: ten 5 MB documents, base64-encoded (4/3 larger), plus the rest of the Bundle.
    /// Kestrel's default of 30 MB would refuse the documented maximum.
    /// </summary>
    public const long MaxSubmitBytes = SupportingDocument.MaxPerSubmission * SupportingDocument.MaxBytes * 4L / 3 + 5 * 1024 * 1024;

    public static void MapFhirEndpoints(this WebApplication app)
    {
        // Public: describes the server and holds no member data.
        app.MapGet("/fhir/metadata", (HttpRequest http, HttpResponse response, IConfiguration config, FhirJson fhir) =>
        {
            response.Headers.ETag = $"W/\"{CapabilityVersion}\"";
            return fhir.Result(Capability(PublicUrl.Base(http, config)));
        });

        // Public: which services need prior authorization, and the documentation each needs.
        // This is the plan's published list, for people and simple tools; EHRs use CRD and DTR for the same information.
        app.MapGet("/requirements", (string? code, string? system, ServiceRequirementCatalog catalog) =>
            Results.Json(catalog.All.Where(r =>
                (code is null || r.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) &&
                (system is null || r.System.Equals(system, StringComparison.Ordinal)))));

        // Da Vinci PAS: submit a prior authorization request.
        app.MapPost("/fhir/Claim/$submit", async (HttpRequest http, ClaimsPrincipal user, IConfiguration config, FhirJson fhir,
            PriorAuthDb db, ServiceRequirementCatalog catalog, PlanClock clock) =>
        {
            var client = await db.ProviderClients.FindAsync(user.ClientId());
            if (client is not { Active: true }) return fhir.Problem("This client is no longer active.", StatusCodes.Status403Forbidden);

            var (bundle, parseError) = fhir.ReadOperationBundle(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var (submission, error) = PasMapper.ReadSubmission(bundle!, catalog);
            if (error is not null) return fhir.Problem(error);

            var requirement = catalog.Find(submission!.ServiceSystem, submission.ServiceCode);
            // The registered name is recorded, not the one the request claims.
            var details = submission.Details(client.ClientId, client.Name, requirement?.Name ?? submission.ServiceDisplay ?? submission.ServiceCode);
            var storedJson = fhir.Write(PasMapper.WithoutDocumentContents(bundle!));
            var now = clock.UtcNow;

            var request = await Find(db, client.ClientId, submission.ProviderRequestSystem, submission.ProviderRequestId);
            if (request is null)
            {
                request = AuthorizationRequest.Receive(details, authorizationRequired: requirement is not null, storedJson, submission.Documents, now);
                db.AuthorizationRequests.Add(request);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Another copy of the same request (a retry sent before the first finished) was stored first.
                    // Answer from that one, as for any resend.
                    db.ChangeTracker.Clear();
                    request = await Find(db, client.ClientId, submission.ProviderRequestSystem, submission.ProviderRequestId);
                    if (request is null) throw;
                    return await Resend(request);
                }
                return fhir.Result(PasMapper.ToResponseBundle(request, bundle!, PublicUrl.Base(http, config), DateTimeOffset.UtcNow));
            }
            return await Resend(request);

            async Task<IResult> Resend(AuthorizationRequest existing)
            {
                // A provider may resend a request (a retry, or to answer an information request), but not reuse its number for something else.
                if (existing.MismatchWith(details) is { } mismatch) return fhir.Problem(mismatch, StatusCodes.Status422UnprocessableEntity);

                var bundlesOnFile = (await db.Set<ReceivedBundle>().Where(b => EF.Property<string>(b, "AuthorizationRequestId") == existing.Id)
                    .Select(b => b.Sha256).ToListAsync()).ToHashSet();
                existing.KeepResubmission(storedJson, now, bundlesOnFile);
                if (existing.Status == AuthorizationStatus.InformationRequested)
                {
                    var documentsOnFile = (await db.SupportingDocuments.Where(d => EF.Property<string>(d, "AuthorizationRequestId") == existing.Id)
                        .Select(d => d.Sha256).ToListAsync()).ToHashSet();
                    existing.SupplyInformation(submission.Documents, now, documentsOnFile);
                }
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A reviewer acted on the request at the same moment. Their change stands; answer with the current state.
                    db.ChangeTracker.Clear();
                    existing = (await Find(db, client.ClientId, submission.ProviderRequestSystem, submission.ProviderRequestId))!;
                }
                // The answer is built from the request as first received, so it always describes what was reviewed.
                return fhir.Result(PasMapper.ToResponseBundle(existing, Stored(existing, fhir), PublicUrl.Base(http, config), DateTimeOffset.UtcNow));
            }
        }).RequireAuthorization(Scopes.SubmitRequests).WithMetadata(new SizeLimit(MaxSubmitBytes));

        // Da Vinci PAS: ask for the current state of one of the caller's own requests, matched on Claim.identifier (system and value).
        app.MapPost("/fhir/Claim/$inquire", async (HttpRequest http, ClaimsPrincipal user, IConfiguration config, FhirJson fhir, PriorAuthDb db) =>
        {
            var (bundle, parseError) = fhir.ReadOperationBundle(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var claim = PasMapper.FindClaim(bundle!);
            if (claim is null) return fhir.Problem("The inquiry Bundle must contain a Claim.");
            var identifier = claim.Identifier.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(identifier?.Value)) return fhir.Problem("The inquiry Claim must carry the identifier of the request being asked about.");

            // A query by example: no match is an empty result, not an error.
            var request = await Find(db, user.ClientId(), identifier.System ?? "", identifier.Value);
            return request is null
                ? fhir.Result(PasMapper.EmptyResultBundle(DateTimeOffset.UtcNow))
                : fhir.Result(PasMapper.ToResponseBundle(request, Stored(request, fhir), PublicUrl.Base(http, config), DateTimeOffset.UtcNow));
        }).RequireAuthorization(Scopes.SubmitRequests);

        // Read one of the caller's own responses. Another provider's response is reported as not found.
        app.MapGet("/fhir/ClaimResponse/{id}", async (string id, HttpRequest http, HttpResponse response, ClaimsPrincipal user,
            IConfiguration config, FhirJson fhir, PriorAuthDb db) =>
        {
            var clientId = user.ClientId();
            var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId);
            if (request is null) return fhir.Problem($"ClaimResponse/{id} was not found.", StatusCodes.Status404NotFound);

            // Pollers can ask "has it changed?" cheaply.
            var etag = $"W/\"{request.Version}\"";
            response.Headers.ETag = etag;
            response.Headers.LastModified = new DateTimeOffset(request.LastChangedAtUtc, TimeSpan.Zero).ToString("R");
            if (http.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);

            return fhir.Result(PasMapper.ToClaimResponse(request, PasMapper.FindClaim(Stored(request, fhir))!, PublicUrl.Base(http, config)));
        }).RequireAuthorization(Scopes.ReadResponses);
    }

    private static Task<AuthorizationRequest?> Find(PriorAuthDb db, string clientId, string system, string value) =>
        db.AuthorizationRequests.FirstOrDefaultAsync(r => r.ClientId == clientId && r.ProviderRequestSystem == system && r.ProviderRequestId == value);

    private static Bundle Stored(AuthorizationRequest request, FhirJson fhir)
    {
        var (bundle, error) = fhir.ReadBundle(request.SubmittedBundleJson);
        return bundle ?? throw new InvalidOperationException($"The Bundle stored for request {request.Id} cannot be read back: {error}");
    }

    private sealed record SizeLimit(long? MaxRequestBodySize) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata;

    private const string CapabilityVersion = "2026-11-11.2";

    private static CapabilityStatement Capability(string baseUrl) => new()
    {
        Url = $"{baseUrl}/fhir/metadata",
        Name = "PriorAuthorizationApi",
        Title = "Prior Authorization API (development build)",
        Status = PublicationStatus.Active,
        Date = "2026-11-11",
        Publisher = "County behavioral health plan (development build)",
        Description = new Markdown("Payer prior authorization API: Da Vinci PAS submit and inquire, DTR questionnaires, and CRD through CDS Hooks at /cds-services. Development build; synthetic data only."),
        Kind = CapabilityStatementKind.Instance,
        Software = new CapabilityStatement.SoftwareComponent { Name = "prior-auth-api", Version = CapabilityVersion },
        Implementation = new CapabilityStatement.ImplementationComponent { Description = "Prior Authorization API (development build)", Url = $"{baseUrl}/fhir" },
        FhirVersion = FHIRVersion.N4_0_1,
        Format = ["json"],
        ImplementationGuide =
        [
            "http://hl7.org/fhir/us/davinci-pas/ImplementationGuide/hl7.fhir.us.davinci-pas|2.0.1",
            "http://hl7.org/fhir/us/davinci-dtr/ImplementationGuide/hl7.fhir.us.davinci-dtr|2.0.1",
            "http://hl7.org/fhir/us/davinci-crd/ImplementationGuide/hl7.fhir.us.davinci-crd|2.0.1",
        ],
        Rest =
        [
            new CapabilityStatement.RestComponent
            {
                Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                Security = new CapabilityStatement.SecurityComponent
                {
                    Service = [new CodeableConcept("http://terminology.hl7.org/CodeSystem/restful-security-service", "SMART-on-FHIR")],
                    Description = "SMART Backend Services. See /fhir/.well-known/smart-configuration.",
                },
                Resource =
                [
                    new CapabilityStatement.ResourceComponent
                    {
                        Type = "Claim",
                        Operation =
                        [
                            new() { Name = "submit", Definition = "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit" },
                            new() { Name = "inquire", Definition = "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquiry" },
                        ],
                    },
                    new CapabilityStatement.ResourceComponent
                    {
                        Type = "ClaimResponse",
                        Interaction = [new() { Code = CapabilityStatement.TypeRestfulInteraction.Read }],
                    },
                    new CapabilityStatement.ResourceComponent
                    {
                        Type = "Questionnaire",
                        Interaction = [new() { Code = CapabilityStatement.TypeRestfulInteraction.Read }],
                        Operation =
                        [
                            new() { Name = "questionnaire-package", Definition = "http://hl7.org/fhir/us/davinci-dtr/OperationDefinition/questionnaire-package" },
                        ],
                    },
                ],
            },
        ],
    };
}
