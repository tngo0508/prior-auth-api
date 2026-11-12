using Hl7.Fhir.Model;

namespace PriorAuth.Api.Domain;

/// <summary>Code systems the plan's service list uses.</summary>
public static class CodeSystems
{
    public const string Hcpcs = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    public const string Cpt = "http://www.ama-assn.org/go/cpt";
}

/// <summary>A covered service that needs prior authorization, with the documentation the plan requires.</summary>
public class ServiceRequirement
{
    /// <summary>The code system of <see cref="Code"/>. A code only matches when its system matches too.</summary>
    public string System { get; init; } = CodeSystems.Hcpcs;
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The Medi-Cal program the service belongs to, for example SMHS or DMC-ODS.</summary>
    public string Program { get; init; } = "";
    public string[] Documentation { get; init; } = [];
    /// <summary>The questions the plan wants answered with a request for this service.</summary>
    public RequirementQuestion[] Questions { get; init; } = [];

    /// <summary>The identifier of this service's questionnaire, for example "prior-auth-h2019".</summary>
    public string QuestionnaireId => $"prior-auth-{Code.ToLowerInvariant()}";
}

/// <summary>One question on a service's questionnaire.</summary>
public class RequirementQuestion
{
    public static readonly string[] Types = ["boolean", "string", "text", "date", "choice"];

    public string LinkId { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>boolean, string, text, date or choice.</summary>
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    /// <summary>The allowed answers when Type is choice.</summary>
    public string[] Options { get; init; } = [];

    /// <summary>The code an answer option is sent with, derived from its text.</summary>
    public static string OptionCode(string option) =>
        new string(option.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}

/// <summary>
/// The list of service requirements, loaded from service-requirements.json.
/// The list is checked when it is built, so a mistake in the file stops the service at start-up
/// rather than failing every call later.
/// </summary>
public class ServiceRequirementCatalog
{
    private readonly Dictionary<(string System, string Code), ServiceRequirement> _byCode;

    public ServiceRequirementCatalog(IEnumerable<ServiceRequirement> requirements)
    {
        var list = requirements.ToList();
        var problems = Validate(list);
        if (problems.Count > 0)
            throw new InvalidOperationException("service-requirements.json is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        _byCode = list.ToDictionary(r => (r.System, r.Code.ToUpperInvariant()));
    }

    public IReadOnlyCollection<ServiceRequirement> All => _byCode.Values;

    /// <summary>The requirement for one coded service. Both the system and the code must match.</summary>
    public ServiceRequirement? Find(string? system, string? code) =>
        system is null || code is null ? null : _byCode.GetValueOrDefault((system, code.Trim().ToUpperInvariant()));

    /// <summary>
    /// The one rule every entry point uses to pick the service from a list of codings:
    /// the first coding that is on the plan's list wins; when none is, the first coding is the service.
    /// </summary>
    public (ServiceRequirement? Requirement, Coding? Coding) Match(IEnumerable<Coding>? codings)
    {
        var list = codings?.Where(c => !string.IsNullOrWhiteSpace(c.Code)).ToList() ?? [];
        foreach (var coding in list)
            if (Find(coding.System, coding.Code) is { } requirement) return (requirement, coding);
        return (null, list.FirstOrDefault());
    }

    public ServiceRequirement? FindByQuestionnaireId(string id) =>
        _byCode.Values.FirstOrDefault(r => r.QuestionnaireId.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static List<string> Validate(List<ServiceRequirement> list)
    {
        var problems = new List<string>();
        foreach (var r in list)
        {
            var name = string.IsNullOrWhiteSpace(r.Code) ? "(an entry with no code)" : r.Code;
            if (string.IsNullOrWhiteSpace(r.Code)) problems.Add($"{name}: Code is empty.");
            if (string.IsNullOrWhiteSpace(r.System)) problems.Add($"{name}: System is empty.");
            if (string.IsNullOrWhiteSpace(r.Name)) problems.Add($"{name}: Name is empty.");
            foreach (var duplicate in r.Questions.GroupBy(q => q.LinkId).Where(g => g.Count() > 1))
                problems.Add($"{name}: question linkId '{duplicate.Key}' is used more than once.");
            foreach (var q in r.Questions)
            {
                if (string.IsNullOrWhiteSpace(q.LinkId)) problems.Add($"{name}: a question has no LinkId.");
                if (!RequirementQuestion.Types.Contains(q.Type, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"{name}/{q.LinkId}: Type '{q.Type}' is not one of {string.Join(", ", RequirementQuestion.Types)}.");
                var isChoice = q.Type.Equals("choice", StringComparison.OrdinalIgnoreCase);
                if (isChoice && q.Options.Length == 0) problems.Add($"{name}/{q.LinkId}: a choice question needs Options.");
                if (!isChoice && q.Options.Length > 0) problems.Add($"{name}/{q.LinkId}: only choice questions can have Options.");
                var codes = q.Options.Select(RequirementQuestion.OptionCode).ToList();
                if (codes.Any(string.IsNullOrEmpty)) problems.Add($"{name}/{q.LinkId}: an option has no letters or digits, so it has no code.");
                foreach (var duplicate in codes.GroupBy(c => c).Where(g => g.Count() > 1))
                    problems.Add($"{name}/{q.LinkId}: two options share the code '{duplicate.Key}'. Reword one of them.");
            }
        }
        foreach (var duplicate in list.GroupBy(r => (r.System, Code: r.Code.ToUpperInvariant())).Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Key.Code} ({duplicate.Key.System}) is listed more than once.");
        return problems;
    }
}
