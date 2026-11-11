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
    public static void MapFhirEndpoints(this WebApplication app)
    {
        // Public: describes the server and holds no member data.
        app.MapGet("/fhir/metadata", (FhirJson fhir) => fhir.Result(Capability()));

        // Public: which services need prior authorization, and the documentation each needs.
        app.MapGet("/requirements", (string? code, ServiceRequirementCatalog catalog) =>
            Results.Json(code is null ? catalog.All : catalog.All.Where(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))));

        // Da Vinci PAS: submit a prior authorization request.
        app.MapPost("/fhir/Claim/$submit", async (HttpRequest http, ClaimsPrincipal user, FhirJson fhir, PriorAuthDb db, ServiceRequirementCatalog catalog, TimeProvider clock) =>
        {
            var client = await db.ProviderClients.FindAsync(user.ClientId());
            if (client is not { Active: true }) return fhir.Problem("This client is no longer active.", StatusCodes.Status403Forbidden);

            var json = await new StreamReader(http.Body).ReadToEndAsync();
            var (bundle, parseError) = fhir.ReadBundle(json);
            if (parseError is not null) return fhir.Problem(parseError);

            var (submission, error) = PasMapper.ReadSubmission(bundle!);
            if (error is not null) return fhir.Problem(error);

            var now = clock.GetUtcNow();
            var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r =>
                r.ClientId == client.ClientId && r.ProviderRequestId == submission!.ProviderRequestId);
            if (request is null)
            {
                var requirement = catalog.Find(submission!.ServiceCode);
                request = AuthorizationRequest.Receive(
                    client.ClientId, submission.ProviderRequestId,
                    providerName: client.Name, // the registered name, not the one the request claims
                    submission.MemberName, submission.ServiceCode,
                    requirement?.Name ?? submission.ServiceDisplay ?? submission.ServiceCode,
                    authorizationRequired: requirement is not null, json, now.UtcDateTime);
                db.AuthorizationRequests.Add(request);
                await db.SaveChangesAsync();
            }

            return fhir.Result(PasMapper.ToResponseBundle(request, submission!.Claim, now));
        }).RequireAuthorization(Scopes.SubmitRequests);

        // Da Vinci PAS: ask for the current state of one of the caller's own requests, matched on Claim.identifier.
        app.MapPost("/fhir/Claim/$inquire", async (HttpRequest http, ClaimsPrincipal user, FhirJson fhir, PriorAuthDb db, TimeProvider clock) =>
        {
            var (bundle, parseError) = fhir.ReadBundle(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var clientId = user.ClientId();
            var identifier = PasMapper.FindClaim(bundle!)?.Identifier.FirstOrDefault()?.Value;
            var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r => r.ClientId == clientId && r.ProviderRequestId == identifier);
            if (request is null) return fhir.Problem($"No request found for Claim.identifier '{identifier}'.", StatusCodes.Status404NotFound);

            return fhir.Result(PasMapper.ToResponseBundle(request, SubmittedClaim(request, fhir), clock.GetUtcNow()));
        }).RequireAuthorization(Scopes.SubmitRequests);

        // Read one of the caller's own responses. Another provider's response is reported as not found.
        app.MapGet("/fhir/ClaimResponse/{id}", async (string id, ClaimsPrincipal user, FhirJson fhir, PriorAuthDb db, TimeProvider clock) =>
        {
            var clientId = user.ClientId();
            var request = await db.AuthorizationRequests.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId);
            return request is null
                ? fhir.Problem($"ClaimResponse/{id} was not found.", StatusCodes.Status404NotFound)
                : fhir.Result(PasMapper.ToClaimResponse(request, SubmittedClaim(request, fhir), clock.GetUtcNow()));
        }).RequireAuthorization(Scopes.ReadResponses);
    }

    private static Claim SubmittedClaim(AuthorizationRequest request, FhirJson fhir) =>
        PasMapper.FindClaim(fhir.ReadBundle(request.SubmittedBundleJson).Bundle!)!;

    private static CapabilityStatement Capability() => new()
    {
        Status = PublicationStatus.Active,
        Date = "2026-11-11",
        Kind = CapabilityStatementKind.Instance,
        FhirVersion = FHIRVersion.N4_0_1,
        Format = ["json"],
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
                ],
            },
        ],
    };
}
