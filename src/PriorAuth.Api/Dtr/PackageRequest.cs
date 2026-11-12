using Hl7.Fhir.Model;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Dtr;

/// <summary>
/// A $questionnaire-package request, read into the questionnaires to return and the warnings to give.
/// The result does not depend on the order of the parameters: questionnaires are listed by id,
/// and the draft response's subject comes from an order when there is one, otherwise from a Coverage.
/// </summary>
public sealed class PackageRequest
{
    public sealed record Package(QuestionnaireDefinition Questionnaire, IReadOnlyList<ServiceRequest> Orders)
    {
        /// <summary>Local references to the orders, for the draft response's qr-context. Orders without an id cannot be referenced.</summary>
        public IReadOnlyList<string> OrderReferences =>
            Orders.Where(o => !string.IsNullOrWhiteSpace(o.Id)).Select(o => $"ServiceRequest/{o.Id}").Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private readonly List<Coverage> _coverages = [];

    public List<string> Warnings { get; } = [];
    public IReadOnlyList<Package> Packages { get; private set; } = [];

    public static PackageRequest Read(Parameters parameters, ServiceRequirementCatalog catalog, string baseUrl)
    {
        var request = new PackageRequest();
        var named = new HashSet<QuestionnaireDefinition>();
        var ordered = new Dictionary<QuestionnaireDefinition, List<ServiceRequest>>();

        foreach (var parameter in parameters.Parameter)
        {
            switch (parameter.Name)
            {
                case "coverage" when parameter.Resource is Coverage coverage:
                    request._coverages.Add(coverage);
                    break;
                case "coverage":
                    request.Warnings.Add($"A 'coverage' parameter held {Describe(parameter.Resource)}, not a Coverage; it was ignored.");
                    break;
                case "questionnaire":
                    if (parameter.Value is PrimitiveType { JsonValue: string canonical } && !string.IsNullOrWhiteSpace(canonical))
                    {
                        if (FindByCanonical(canonical, catalog, baseUrl, out var warning) is { } questionnaire) named.Add(questionnaire);
                        else request.Warnings.Add(warning!);
                    }
                    else request.Warnings.Add("A 'questionnaire' parameter had no canonical value; it was ignored.");
                    break;
                case "order" when parameter.Resource is ServiceRequest order:
                    // Orders carry no billing modifiers, so a code whose entries differ by modifier cannot pick a questionnaire.
                    var match = catalog.Match(order.Code?.Coding, modifiers: null);
                    var chosen = match.Kind == MatchKind.Listed ? catalog.FindQuestionnaire(match.Requirement!.Questionnaire) : null;
                    if (chosen is null)
                    {
                        request.Warnings.Add($"No questionnaire applies to {Describe(order)} ({match.Coding?.Code ?? "no code"}): {WhyNone(match)}");
                        break;
                    }
                    if (!ordered.TryGetValue(chosen, out var orders)) ordered[chosen] = orders = [];
                    orders.Add(order);
                    break;
                case "order":
                    request.Warnings.Add($"{Describe(parameter.Resource)} was sent as an order. Only ServiceRequest orders are supported, so no questionnaire was chosen for it.");
                    break;
            }
        }

        if (request._coverages.Count == 0)
            request.Warnings.Add("No 'coverage' parameter was sent. DTR requires the Coverage the documentation is for; the draft responses do not name one.");

        request.Packages = named.Concat(ordered.Keys).Distinct()
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .Select(q => new Package(q, ordered.TryGetValue(q, out var orders) ? orders : []))
            .ToList();
        return request;
    }

    /// <summary>The patient: the subject of an order when one was sent, otherwise the beneficiary of a Coverage.</summary>
    public ResourceReference? SubjectFor(Package package)
    {
        var fromOrder = package.Orders
            .OrderBy(o => o.Id ?? "", StringComparer.Ordinal)
            .Select(o => o.Subject).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s?.Reference));
        var subject = fromOrder ?? _coverages
            .OrderBy(c => c.Id ?? "", StringComparer.Ordinal)
            .Select(c => c.Beneficiary).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b?.Reference));
        return subject is null ? null : new ResourceReference(subject.Reference, subject.Display);
    }

    /// <summary>The Coverages the draft response is for: those sent, or failing that the ones the orders name.</summary>
    public IReadOnlyList<string> CoverageReferencesFor(Package package)
    {
        var sent = _coverages.Where(c => !string.IsNullOrWhiteSpace(c.Id)).Select(c => $"Coverage/{c.Id}");
        var named = package.Orders.SelectMany(o => o.Insurance).Select(i => i.Reference!).Where(r => !string.IsNullOrWhiteSpace(r));
        var list = _coverages.Count > 0 ? sent : named;
        return list.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Matches a canonical such as https://host/fhir/Questionnaire/dtr-mhp-youth|1 on its full URL (this server's
    /// base plus the questionnaire id) and, when given, its version.
    /// </summary>
    private static QuestionnaireDefinition? FindByCanonical(string canonical, ServiceRequirementCatalog catalog, string baseUrl, out string? warning)
    {
        var bar = canonical.IndexOf('|');
        var url = (bar < 0 ? canonical : canonical[..bar]).Trim();
        var version = bar < 0 ? null : canonical[(bar + 1)..].Trim();

        var questionnaire = catalog.Questionnaires.FirstOrDefault(q => QuestionnaireBuilder.Url(q.Id, baseUrl) == url);
        if (questionnaire is null)
        {
            warning = $"Questionnaire {canonical} was not found on this server.";
            return null;
        }
        if (!string.IsNullOrEmpty(version) && version != QuestionnaireBuilder.Version)
        {
            warning = $"Questionnaire {canonical} was not found: version {version} is not available (the current version is {QuestionnaireBuilder.Version}).";
            return null;
        }
        warning = null;
        return questionnaire;
    }

    private static string WhyNone(ServiceMatch match) => match.Kind switch
    {
        // A reviewed service always has a questionnaire (checked at start-up), so a listed service here is one the plan may not review.
        MatchKind.Listed => ServiceRequirement.MayNotRequire,
        MatchKind.Ambiguous => "the code covers services with different rules, told apart by billing modifiers an order cannot carry. Name the questionnaire instead.",
        MatchKind.NotListed => "the service is not on the plan's list, so it needs no prior authorization.",
        _ => "the service code was not recognised. Check the code and its code system.",
    };

    private static string Describe(Resource? resource) => resource switch
    {
        null => "nothing",
        _ when string.IsNullOrWhiteSpace(resource.Id) => $"a {resource.TypeName} with no id",
        _ => $"{resource.TypeName}/{resource.Id}",
    };
}
