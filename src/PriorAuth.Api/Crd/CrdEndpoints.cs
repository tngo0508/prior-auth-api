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
/// order-sign is a CRD "primary" hook: every checked order gets coverage information, including what
/// documentation is needed. order-select is "secondary": it answers only the orders in context.selections
/// and never says that documentation or information is needed (CRD 2.0.1 hooks.html).
/// This service does not check whether the member is enrolled, so "covered" is always "conditional".
/// </summary>
public static class CrdEndpoints
{
    public const string CoverageInformationUrl = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    /// <summary>The CRD temporary code system, which holds the card types and the DocReason codes in CRD 2.0.1.</summary>
    public const string CrdTempSystem = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";

    /// <summary>The CRD card type of everything this service returns, and the code of its one configuration option.</summary>
    public const string CoverageInfoCardType = "coverage-info";

    public const string ConfigurationOptionsExtension = "davinci-crd.configuration-options";

    private const string DefaultPlanName = "Behavioral Health Plan (synthetic)";
    private const int MaxSummaryLength = 139; // CDS Hooks: summary is "<140-character".

    private record CdsService(string Id, string Hook, string Title, bool IsPrimaryHook);

    private static readonly CdsService[] Services =
    [
        new("prior-auth-order-sign", "order-sign", "Prior authorization requirements when an order is signed", IsPrimaryHook: true),
        new("prior-auth-order-select", "order-select", "Prior authorization requirements while an order is being written", IsPrimaryHook: false),
    ];

    /// <summary>CRD: at least one boolean option per card type returned, each with code, type, name, description and default.</summary>
    private static readonly object[] ConfigurationOptions =
    [
        new
        {
            code = CoverageInfoCardType,
            type = "boolean",
            name = "Coverage information",
            description = "Whether orders are annotated with coverage information: if prior authorization is needed and which questionnaire documents it.",
            @default = true,
        },
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
                extension = new Dictionary<string, object> { [ConfigurationOptionsExtension] = ConfigurationOptions },
            }),
        }));

        app.MapPost("/cds-services/{id}", async (string id, HttpRequest http, ClientAssertions assertions, FhirJson fhir,
            ServiceRequirementCatalog catalog, PlanClock planClock, IConfiguration config, ILoggerFactory loggers) =>
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

            CdsHookRequest? request;
            string? shapeError;
            try
            {
                using var document = await JsonDocument.ParseAsync(http.Body);
                (request, shapeError) = CdsHookRequest.Read(document.RootElement, service.Hook, selectionsRequired: service.Hook == "order-select");
            }
            catch (JsonException) { return fhir.Problem("The request body is not JSON.", StatusCodes.Status400BadRequest); }
            if (shapeError is not null) return fhir.Problem(shapeError, StatusCodes.Status422UnprocessableEntity);

            var (orders, parseError) = fhir.ReadBundle(request!.DraftOrdersJson);
            if (parseError is not null) return fhir.Problem(parseError, StatusCodes.Status422UnprocessableEntity);

            var logger = loggers.CreateLogger("PriorAuth.Api.Crd");
            var response = new CrdResponder(service.IsPrimaryHook, request, fhir, catalog, baseUrl,
                planClock.Today.ToString("yyyy-MM-dd"), config["Plan:Name"] ?? DefaultPlanName, logger);
            return Results.Json(response.Answer(orders!));
        });
    }

    /// <summary>Works out the cards and system actions for one hook call.</summary>
    private sealed class CrdResponder(
        bool primaryHook, CdsHookRequest request, FhirJson fhir, ServiceRequirementCatalog catalog,
        string baseUrl, string today, string planName, ILogger logger)
    {
        private readonly JsonArray _cards = [];
        private readonly JsonArray _actions = [];

        public JsonObject Answer(Bundle orders)
        {
            if (!CoverageInfoEnabled())
            {
                // Everything this service returns is coverage information, so with it turned off there is nothing to say.
                logger.LogInformation("CRD hook {HookInstance}: coverage-info turned off by the client's configuration; nothing returned.", request.HookInstance);
                return Result();
            }

            foreach (var resource in orders.Entry.Select(e => e.Resource).OfType<Resource>())
            {
                if (resource is not ServiceRequest order)
                {
                    logger.LogWarning("CRD hook {HookInstance}: coverage could not be checked for {Order}: only ServiceRequest orders are supported.",
                        request.HookInstance, Describe(resource));
                    continue;
                }
                // order-select: answer only the orders the clinician has selected.
                if (request.Selections is not null && !request.Selections.Contains($"ServiceRequest/{order.Id}")) continue;
                AnswerOrder(order);
            }
            return Result();
        }

        private JsonObject Result()
        {
            var result = new JsonObject { ["cards"] = _cards };
            if (_actions.Count > 0) result["systemActions"] = _actions; // CDS Hooks: optional attributes without a value are omitted.
            return result;
        }

        private void AnswerOrder(ServiceRequest order)
        {
            // Orders carry no billing modifiers, so entries that differ only by modifier are answered together.
            var match = catalog.Match(order.Code?.Coding, modifiers: null);
            var requirement = match.Requirement;
            var service = match.Kind switch
            {
                // Entries that differ only by modifier and agree on the answer (H2012) are named together.
                MatchKind.Listed => $"{match.Coding!.Code.Trim()} {string.Join(" or ", match.Candidates.Select(c => c.Name))}",
                MatchKind.Ambiguous => $"{match.Coding!.Code} ({string.Join(" or ", match.Candidates.Select(c => c.Name))})",
                _ => match.Coding?.Code ?? "the ordered service",
            };
            var orderName = $"{Describe(order)} ({service})";

            // The CRD ServiceRequest profile requires authoredOn and requester. The order is echoed back in the update,
            // so an order without them is explained instead of annotated.
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(order.AuthoredOn)) missing.Add("authoredOn");
            if (order.Requester is null) missing.Add("requester");
            if (missing.Count > 0)
            {
                logger.LogWarning("CRD hook {HookInstance}: coverage could not be checked for {Order}: missing {Missing}.",
                    request.HookInstance, Describe(order), string.Join(", ", missing));
                _cards.Add(Card($"Coverage not checked for {service}: order incomplete",
                    $"Prior authorization requirements were not attached to {orderName} because the order has no {string.Join(" or ", missing)}. Add it and sign again."));
                return;
            }

            var coverage = order.Insurance.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Reference))
                ?? (request.PrefetchedCoverageId is { } coverageId ? new ResourceReference($"Coverage/{coverageId}") : null);
            if (coverage is null)
            {
                // The extension must name the coverage it speaks for, so without one we can only explain.
                logger.LogWarning("CRD hook {HookInstance}: coverage could not be checked for {Order}: no coverage was sent.",
                    request.HookInstance, Describe(order));
                _cards.Add(Card($"Coverage not supplied for {service}",
                    $"Prior authorization requirements could not be attached to {orderName} because no coverage was sent with it."));
                return;
            }

            var verdict = Decide(match, baseUrl);
            var information = new Extension { Url = CoverageInformationUrl };
            information.Extension.Add(new Extension("coverage", new ResourceReference(coverage.Reference)));
            // This service does not check enrolment, so it never asserts coverage: "conditional" on the member's
            // enrolment in the plan's program, for every answer.
            information.Extension.Add(new Extension("covered", new Code("conditional")));
            information.Extension.Add(new Extension("pa-needed", new Code(verdict.PaNeeded)));
            // Secondary hooks (order-select) SHALL NOT say that clinical or administrative information is needed.
            if (verdict.Questionnaire is not null && primaryHook)
            {
                information.Extension.Add(new Extension("doc-needed", new Code("clinical")));
                information.Extension.Add(new Extension("doc-purpose", new Code("withpa")));
                information.Extension.Add(new Extension("questionnaire", new Canonical(verdict.Questionnaire)));
            }
            if (verdict.Reason is not null)
                information.Extension.Add(new Extension("reason", new CodeableConcept { Text = verdict.Reason }));

            var existing = order.Extension.Where(e => e.Url == CoverageInformationUrl && CoverageOf(e) == coverage.Reference).ToList();
            // CRD: do not send an update unless something is new.
            if (existing.Count == 1 && SameAssertion(existing[0], information)) return;

            information.Extension.Add(new Extension("date", new Date(today)));
            information.Extension.Add(new Extension("coverage-assertion-id", new FhirString(Guid.NewGuid().ToString("N"))));

            // Replace only what was said for this coverage; annotations for other coverages stay.
            order.Extension.RemoveAll(e => existing.Contains(e));
            order.Extension.Add(information);

            _actions.Add(new JsonObject
            {
                ["type"] = "update",
                ["description"] = verdict.PaNeeded switch
                {
                    "auth-needed" => $"Prior authorization is needed for {service}.",
                    "no-auth" => $"No prior authorization is needed for {service}.",
                    _ => $"Whether prior authorization is needed for {service} could not be decided.",
                },
                ["resource"] = JsonNode.Parse(fhir.Write(order)),
            });
        }

        private sealed record Verdict(string PaNeeded, string? Questionnaire, string? Reason);

        /// <summary>What CRD says about one order, from the rule on the plan's list.</summary>
        private static Verdict Decide(ServiceMatch match, string baseUrl)
        {
            switch (match.Kind)
            {
                case MatchKind.Listed:
                    var requirement = match.Requirement!;
                    return requirement.Rule switch
                    {
                        ReviewRule.PriorAuthorization => new Verdict("auth-needed", QuestionnaireBuilder.Url(requirement.Questionnaire!, baseUrl), null),
                        ReviewRule.ConcurrentReview => new Verdict("no-auth", null,
                            "No prior authorization is needed: the stay is reviewed concurrently. Notify the plan within 24 hours of admission " +
                            $"by sending the admission through $submit, with questionnaire {QuestionnaireBuilder.Url(requirement.Questionnaire!, baseUrl)}."),
                        _ => new Verdict("no-auth", null, ServiceRequirement.MayNotRequire),
                    };
                case MatchKind.Ambiguous:
                    var needing = match.Candidates.Where(c => c.Rule != ReviewRule.NoPriorAuthorization).Select(c => c.Describe()).ToList();
                    return new Verdict("conditional", null,
                        $"Whether prior authorization is needed depends on the billing modifier. Needs review: {string.Join(", ", needing)}. " +
                        "Other modifiers: no prior authorization.");
                case MatchKind.NotListed:
                    return new Verdict("no-auth", null, "The service is not on the plan's list of services that need prior authorization.");
                default:
                    return new Verdict("conditional", null,
                        "The service code or its code system was not recognised, so the plan cannot say whether prior authorization is needed. Check the code or contact the plan.");
            }
        }

        /// <summary>
        /// The client's "coverage-info" option. Absent means the default (on). A value that is not a boolean
        /// cannot be honoured, so it is logged and the default is used.
        /// </summary>
        private bool CoverageInfoEnabled()
        {
            if (!request.Configuration.TryGetValue(CoverageInfoCardType, out var value)) return true;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            logger.LogWarning("CRD hook {HookInstance}: configuration option {Option} must be a boolean; using the default (true).",
                request.HookInstance, CoverageInfoCardType);
            return true;
        }

        private JsonObject Card(string summary, string detail) => new()
        {
            ["uuid"] = Guid.NewGuid().ToString(),
            ["summary"] = summary.Length <= MaxSummaryLength ? summary : summary[..(MaxSummaryLength - 3)] + "...",
            ["detail"] = detail,
            ["indicator"] = "info",
            ["source"] = new JsonObject
            {
                ["label"] = planName,
                ["topic"] = new JsonObject { ["system"] = CrdTempSystem, ["code"] = CoverageInfoCardType, ["display"] = "Coverage Information" },
            },
        };

        private static string Describe(Resource resource) =>
            string.IsNullOrWhiteSpace(resource.Id) ? $"a {resource.TypeName} with no id" : $"{resource.TypeName}/{resource.Id}";

        private static string? CoverageOf(Extension information) =>
            (information.GetExtension("coverage")?.Value as ResourceReference)?.Reference;

        /// <summary>Whether two coverage-information extensions say the same thing, ignoring their date and assertion id.</summary>
        private static bool SameAssertion(Extension inbound, Extension computed)
        {
            static List<Extension> Content(Extension e) =>
                e.Extension.Where(x => x.Url is not ("date" or "coverage-assertion-id")).ToList();

            var unmatched = Content(inbound);
            var wanted = Content(computed);
            if (unmatched.Count != wanted.Count) return false;
            foreach (var item in wanted)
            {
                var match = unmatched.FirstOrDefault(u => u.Url == item.Url && u.Value is not null && item.Value is not null && u.Value.IsExactly(item.Value));
                if (match is null) return false;
                unmatched.Remove(match);
            }
            return true;
        }
    }
}
