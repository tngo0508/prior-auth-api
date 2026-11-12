using Hl7.Fhir.Model;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Fhir;

namespace PriorAuth.Api.Dtr;

/// <summary>
/// Da Vinci Documentation Templates and Rules (DTR): the provider's system downloads the
/// questionnaire for a service, a person completes it, and the answers go in with the request.
/// </summary>
public static class DtrEndpoints
{
    /// <summary>
    /// The name of the warnings parameter in the $questionnaire-package response. The DTR 2.0.1 output profile
    /// (dtr-qpackage-output-parameters) fixes it as "operationOutcome"; the OperationDefinition's table calls it "outcome".
    /// The response follows the output profile, as it already does for "PackageBundle".
    /// </summary>
    public const string OutcomeParameter = "operationOutcome";

    public static void MapDtrEndpoints(this WebApplication app)
    {
        app.MapGet("/fhir/Questionnaire/{id}", (string id, HttpRequest http, FhirJson fhir, ServiceRequirementCatalog catalog, IConfiguration config) =>
        {
            var requirement = catalog.FindByQuestionnaireId(id);
            return requirement is null
                ? fhir.Problem($"Questionnaire/{id} was not found.", StatusCodes.Status404NotFound)
                : fhir.Result(QuestionnaireBuilder.Build(requirement, PublicUrl.Base(http, config)));
        }).RequireAuthorization(Scopes.ReadQuestionnaires);

        // DTR $questionnaire-package: name the questionnaires you want, or send the orders and let the plan choose.
        app.MapPost("/fhir/Questionnaire/$questionnaire-package", async (HttpRequest http, FhirJson fhir, ServiceRequirementCatalog catalog,
            IConfiguration config, TimeProvider clock) =>
        {
            var (parameters, parseError) = fhir.Read<Parameters>(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var baseUrl = PublicUrl.Base(http, config);
            var request = PackageRequest.Read(parameters!, catalog, baseUrl);

            if (request.Packages.Count == 0)
            {
                var notFound = Outcome(request.Warnings);
                notFound.Issue.Insert(0, new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.NotFound,
                    Diagnostics = "No questionnaire applies to what was sent. Send a 'questionnaire' canonical or an 'order' for a service that needs prior authorization.",
                });
                return fhir.Result(notFound, StatusCodes.Status404NotFound);
            }

            var authored = clock.GetUtcNow();
            var response = new Parameters
            {
                Parameter = request.Packages.Select(p => new Parameters.ParameterComponent
                {
                    Name = "PackageBundle",
                    Resource = QuestionnaireBuilder.Package(p.Requirement, baseUrl,
                        new DraftContext(request.SubjectFor(p), request.CoverageReferencesFor(p), p.OrderReferences, authored)),
                }).ToList(),
            };
            if (request.Warnings.Count > 0)
                response.Parameter.Add(new Parameters.ParameterComponent { Name = OutcomeParameter, Resource = Outcome(request.Warnings) });
            return fhir.Result(response);
        }).RequireAuthorization(Scopes.ReadQuestionnaires);

        // DTR $log-questionnaire-errors: a DTR app reports problems it had with one of our questionnaires.
        // The operation defines no output, so the reply is a short informational OperationOutcome.
        app.MapPost("/fhir/Questionnaire/$log-questionnaire-errors", async (HttpRequest http, FhirJson fhir, ILoggerFactory loggers) =>
        {
            var (parameters, parseError) = fhir.Read<Parameters>(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var questionnaire = parameters!.Parameter.FirstOrDefault(p => p.Name == "Questionnaire")?.Value as PrimitiveType;
            var reported = parameters.Parameter.FirstOrDefault(p => p.Name == "OperationOutcome")?.Resource as OperationOutcome;
            if (questionnaire?.JsonValue is not string canonical || string.IsNullOrWhiteSpace(canonical))
                return fhir.Problem("The 'Questionnaire' parameter (a canonical) is required.");
            if (reported is null)
                return fhir.Problem("The 'OperationOutcome' parameter is required.");

            // Logged for staff who maintain the questionnaires. The client SHALL NOT put PHI in these.
            var logger = loggers.CreateLogger("PriorAuth.Api.Dtr");
            var clientId = http.HttpContext.User.ClientId();
            foreach (var issue in reported.Issue)
                logger.LogWarning("DTR questionnaire error reported by client {ClientId} for {Questionnaire}: {Severity} {Code} at {Location}: {Diagnostics}",
                    clientId, canonical, issue.Severity, issue.Code, string.Join(", ", issue.Expression ?? []), issue.Diagnostics ?? issue.Details?.Text);
            if (reported.Issue.Count == 0)
                logger.LogWarning("DTR questionnaire error report from client {ClientId} for {Questionnaire} had no issues.", clientId, canonical);

            return fhir.Result(new OperationOutcome
            {
                Issue =
                [
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Information,
                        Code = OperationOutcome.IssueType.Informational,
                        Diagnostics = $"{reported.Issue.Count} issue(s) for {canonical} were recorded.",
                    },
                ],
            });
        }).RequireAuthorization(Scopes.ReadQuestionnaires);

        // ValueSet $expand. The DTR payer CapabilityStatement requires it. The questionnaires carry their answer
        // options inline and the plan publishes no ValueSets, so every value set is "not found".
        app.MapMethods("/fhir/ValueSet/$expand", ["GET", "POST"], (HttpRequest http, FhirJson fhir) =>
        {
            var url = http.Query["url"].ToString();
            return fhir.Problem(
                string.IsNullOrWhiteSpace(url)
                    ? "This server publishes no value sets, so none can be expanded."
                    : $"ValueSet {url} is not known to this server. It publishes no value sets; its questionnaires list their answers inline.",
                StatusCodes.Status404NotFound);
        }).RequireAuthorization(Scopes.ReadQuestionnaires);
    }

    private static OperationOutcome Outcome(IEnumerable<string> warnings) => new()
    {
        Issue = warnings.Select(w => new OperationOutcome.IssueComponent
        {
            Severity = OperationOutcome.IssueSeverity.Warning,
            Code = OperationOutcome.IssueType.NotFound,
            Diagnostics = w,
        }).ToList(),
    };
}
