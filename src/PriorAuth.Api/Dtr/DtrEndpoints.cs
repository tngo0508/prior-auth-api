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
        app.MapPost("/fhir/Questionnaire/$questionnaire-package", async (HttpRequest http, FhirJson fhir, ServiceRequirementCatalog catalog, IConfiguration config) =>
        {
            var (parameters, parseError) = fhir.Read<Parameters>(await new StreamReader(http.Body).ReadToEndAsync());
            if (parseError is not null) return fhir.Problem(parseError);

            var baseUrl = PublicUrl.Base(http, config);
            var wanted = new List<(ServiceRequirement Requirement, ResourceReference? Subject)>();

            foreach (var parameter in parameters!.Parameter)
            {
                if (parameter.Name == "questionnaire" && parameter.Value is PrimitiveType { ObjectValue: string canonical })
                {
                    // A canonical looks like https://host/fhir/Questionnaire/prior-auth-h2019|1
                    var id = canonical.Split('|')[0].TrimEnd('/').Split('/').Last();
                    if (catalog.FindByQuestionnaireId(id) is { } named) wanted.Add((named, null));
                }
                else if (parameter.Name == "order" && parameter.Resource is ServiceRequest order)
                {
                    var requirement = order.Code?.Coding.Select(c => catalog.Find(c.Code)).FirstOrDefault(r => r is not null);
                    if (requirement is not null) wanted.Add((requirement, order.Subject));
                }
            }

            var packages = wanted.DistinctBy(w => w.Requirement.Code).ToList();
            if (packages.Count == 0)
                return fhir.Problem("No questionnaire applies to what was sent. Send a 'questionnaire' canonical or an 'order' for a service that needs prior authorization.", StatusCodes.Status404NotFound);

            return fhir.Result(new Parameters
            {
                Parameter = packages.Select(p => new Parameters.ParameterComponent
                {
                    Name = "PackageBundle",
                    Resource = QuestionnaireBuilder.Package(p.Requirement, baseUrl, p.Subject),
                }).ToList(),
            });
        }).RequireAuthorization(Scopes.ReadQuestionnaires);
    }
}
