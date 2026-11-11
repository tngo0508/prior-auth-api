using System.Text.Json;
using System.Text.Json.Nodes;
using Hl7.Fhir.Model;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Dtr;
using PriorAuth.Api.Fhir;

namespace PriorAuth.Api.Crd;

/// <summary>
/// Da Vinci Coverage Requirements Discovery (CRD), built on CDS Hooks.
/// While a clinician is writing an order, the EHR calls these services and is told, for each order,
/// whether prior authorization is needed and which questionnaire to complete.
/// The answer travels as a "coverage information" extension added to the order.
///
/// NOTE: built from the published CRD 2.0.1 definitions. The CDS Hooks request shape and its JWT
/// rules were written from knowledge of that specification, not re-read during this work.
/// This service does not check whether the member is eligible; see the "covered" value below.
/// </summary>
public static class CrdEndpoints
{
    public const string CoverageInformationUrl = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    private record CdsService(string Id, string Hook, string Title);

    private static readonly CdsService[] Services =
    [
        new("prior-auth-order-sign", "order-sign", "Prior authorization requirements when an order is signed"),
        new("prior-auth-order-select", "order-select", "Prior authorization requirements while an order is being written"),
    ];

    public static void MapCrdEndpoints(this WebApplication app)
    {
        // CDS Hooks discovery. Public: it only describes the services.
        app.MapGet("/cds-services", () => Results.Json(new
        {
            services = Services.Select(s => new
            {
                hook = s.Hook,
                id = s.Id,
                title = s.Title,
                description = "Tells the ordering system whether each ordered service needs prior authorization from the plan and which questionnaire documents the request.",
                prefetch = new Dictionary<string, string> { ["coverage"] = "Coverage?patient={{context.patientId}}&status=active" },
            }),
        }));

        app.MapPost("/cds-services/{id}", async (string id, HttpRequest http, ClientAssertions assertions, FhirJson fhir,
            ServiceRequirementCatalog catalog, TimeProvider clock, IConfiguration config) =>
        {
            var service = Services.FirstOrDefault(s => s.Id == id);
            if (service is null) return fhir.Problem($"There is no CDS service '{id}'.", StatusCodes.Status404NotFound);

            // CDS Hooks: every call carries a JWT signed by the calling system, made for this service's address.
            var baseUrl = PublicUrl.Base(http, config);
            var authorization = http.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return fhir.Problem("A signed bearer token is required.", StatusCodes.Status401Unauthorized);
            var (client, authError) = await assertions.ValidateAsync(authorization["Bearer ".Length..].Trim(), $"{baseUrl}/cds-services/{id}", subjectMustMatchIssuer: false);
            if (client is null) return fhir.Problem(authError!, StatusCodes.Status401Unauthorized);

            JsonElement request;
            try { request = (await JsonDocument.ParseAsync(http.Body)).RootElement; }
            catch (JsonException) { return fhir.Problem("The request body is not JSON.", StatusCodes.Status400BadRequest); }

            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("hook", out var hook) || hook.GetString() != service.Hook)
                return fhir.Problem($"hook must be '{service.Hook}' for this service.", StatusCodes.Status422UnprocessableEntity);
            if (!request.TryGetProperty("context", out var context) || !context.TryGetProperty("draftOrders", out var draftOrders))
                return fhir.Problem("context.draftOrders is required.", StatusCodes.Status422UnprocessableEntity);

            var (orders, parseError) = fhir.ReadBundle(draftOrders.GetRawText());
            if (parseError is not null) return fhir.Problem(parseError, StatusCodes.Status422UnprocessableEntity);

            var prefetchedCoverage = PrefetchedCoverage(request);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
            var cards = new JsonArray();
            var actions = new JsonArray();

            foreach (var order in orders!.Entry.Select(e => e.Resource).OfType<ServiceRequest>())
            {
                var codes = order.Code?.Coding.Select(c => c.Code).ToList() ?? [];
                var requirement = codes.Select(catalog.Find).FirstOrDefault(r => r is not null);
                var label = requirement is null ? codes.FirstOrDefault() ?? "the ordered service" : $"{requirement.Code} {requirement.Name}";

                var coverage = order.Insurance.FirstOrDefault() ?? prefetchedCoverage;
                if (coverage is null)
                {
                    // The extension must name the coverage it speaks for, so without one we can only explain.
                    cards.Add(new JsonObject
                    {
                        ["uuid"] = Guid.NewGuid().ToString(),
                        ["summary"] = $"Coverage not supplied for {label}",
                        ["detail"] = "Prior authorization requirements could not be attached to the order because no coverage was sent with it.",
                        ["indicator"] = "warning",
                        ["source"] = new JsonObject { ["label"] = "Behavioral Health Plan prior authorization" },
                    });
                    continue;
                }

                var information = new Extension { Url = CoverageInformationUrl };
                information.Extension.Add(new Extension("coverage", coverage));
                // "covered" is stated only for services on the plan's list. For anything else this service
                // does not know, so it says "conditional" and never asserts coverage it has not checked.
                information.Extension.Add(new Extension("covered", new Code(requirement is null ? "conditional" : "covered")));
                information.Extension.Add(new Extension("pa-needed", new Code(requirement is null ? "no-auth" : "auth-needed")));
                if (requirement is not null)
                {
                    information.Extension.Add(new Extension("doc-needed", new Code("clinical")));
                    information.Extension.Add(new Extension("questionnaire", new Canonical(QuestionnaireBuilder.Url(requirement, baseUrl))));
                }
                information.Extension.Add(new Extension("date", new Date(today)));
                information.Extension.Add(new Extension("coverage-assertion-id", new FhirString(Guid.NewGuid().ToString("N"))));

                order.Extension.RemoveAll(e => e.Url == CoverageInformationUrl);
                order.Extension.Add(information);

                actions.Add(new JsonObject
                {
                    ["type"] = "update",
                    ["description"] = requirement is null
                        ? $"No prior authorization is needed for {label}."
                        : $"Prior authorization is needed for {label}.",
                    ["resource"] = JsonNode.Parse(fhir.Write(order)),
                });
            }

            return Results.Json(new JsonObject { ["cards"] = cards, ["systemActions"] = actions });
        });
    }

    /// <summary>The EHR may send the member's coverage ahead of time, as a Coverage or a search result Bundle.</summary>
    private static ResourceReference? PrefetchedCoverage(JsonElement request)
    {
        if (!request.TryGetProperty("prefetch", out var prefetch) || prefetch.ValueKind != JsonValueKind.Object ||
            !prefetch.TryGetProperty("coverage", out var coverage) || coverage.ValueKind != JsonValueKind.Object)
            return null;

        if (coverage.TryGetProperty("resourceType", out var type) && type.GetString() == "Bundle" &&
            coverage.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0 &&
            entries[0].TryGetProperty("resource", out var first))
            coverage = first;

        return coverage.TryGetProperty("resourceType", out type) && type.GetString() == "Coverage" &&
               coverage.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } coverageId
            ? new ResourceReference($"Coverage/{coverageId}")
            : null;
    }
}
