using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

namespace PriorAuth.Api.Domain;

/// <summary>Code systems the plan's service list uses, and what a well-formed code looks like in each.</summary>
public static partial class CodeSystems
{
    public const string Hcpcs = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    public const string Cpt = "http://www.ama-assn.org/go/cpt";
    /// <summary>UB-04 revenue codes (NUBC), which inpatient hospitals bill with. The canonical URL is the one HL7 Terminology lists.</summary>
    public const string RevenueCode = "https://www.nubc.org/CodeSystem/RevenueCodes";

    /// <summary>Whether the code is well formed for a system this plan knows. Codes are compared trimmed and upper case.</summary>
    public static bool IsRecognised(string? system, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = code.Trim().ToUpperInvariant();
        return system switch
        {
            // HCPCS Level II codes are a letter and four digits; Level I (CPT) codes are sometimes sent under HCPCS too.
            Hcpcs => HcpcsPattern().IsMatch(normalized) || CptPattern().IsMatch(normalized),
            Cpt => CptPattern().IsMatch(normalized),
            RevenueCode => RevenuePattern().IsMatch(normalized),
            _ => false,
        };
    }

    [GeneratedRegex("^[A-Z][0-9]{4}$")] private static partial Regex HcpcsPattern();
    [GeneratedRegex("^[0-9]{4}[0-9A-Z]$")] private static partial Regex CptPattern();
    [GeneratedRegex("^[0-9]{4}$")] private static partial Regex RevenuePattern();
}

/// <summary>What the plan does with a request for a listed service.</summary>
public enum ReviewRule
{
    /// <summary>Approval is needed before the service is given.</summary>
    PriorAuthorization,
    /// <summary>No approval beforehand: the provider notifies the plan after admission and the stay is reviewed while it happens (BHIN 22-017).</summary>
    ConcurrentReview,
    /// <summary>The plan may not require approval. Listed so the answer is stated, not guessed.</summary>
    NoPriorAuthorization,
}

/// <summary>
/// One coded service on the plan's list and the rule that applies to it.
/// A code may appear more than once with different modifiers, for example the ASAM residential levels, which share H0019.
/// </summary>
public class ServiceRequirement
{
    /// <summary>The code system of <see cref="Code"/>. A code only matches when its system matches too.</summary>
    public string System { get; init; } = CodeSystems.Hcpcs;
    public string Code { get; init; } = "";
    /// <summary>Billing modifiers that must be on the request for this entry to apply, for example "U1" for ASAM 3.1.</summary>
    public string[] Modifiers { get; init; } = [];
    public string Name { get; init; } = "";
    /// <summary>The Medi-Cal program the service belongs to: SMHS (the Mental Health Plan) or DMC-ODS.</summary>
    public string Program { get; init; } = "";
    public ReviewRule Rule { get; init; } = ReviewRule.PriorAuthorization;
    /// <summary>The id of the questionnaire that documents a request. Required when the service is reviewed; never set otherwise.</summary>
    public string? Questionnaire { get; init; }
    /// <summary>False while the code or modifier has not been confirmed against the DHCS billing manual.</summary>
    public bool CodeConfirmed { get; init; } = true;
    /// <summary>Where the rule and code come from, for example "BHIN 26-030 p.8".</summary>
    public string Source { get; init; } = "";
    public string? Note { get; init; }

    /// <summary>What the plan says about a listed service it may not prior-authorize.</summary>
    public const string MayNotRequire = "The plan may not require prior authorization for this service.";

    /// <summary>The code with its modifiers, for example "H0019:U1".</summary>
    public string Describe() => NormalizedModifiers.Length == 0 ? NormalizedCode : $"{NormalizedCode}:{string.Join(":", NormalizedModifiers)}";

    internal string NormalizedCode => Code.Trim().ToUpperInvariant();
    internal string[] NormalizedModifiers => Modifiers.Select(m => m.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>A questionnaire the plan uses to document requests. Several services may share one.</summary>
public class QuestionnaireDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>"draft" until the plan's clinical lead signs it off, then "active".</summary>
    public string Status { get; init; } = "draft";
    /// <summary>The documents the plan expects with a request.</summary>
    public string[] Documentation { get; init; } = [];
    public RequirementQuestion[] Questions { get; init; } = [];

    public bool IsDraft => !Status.Equals("active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One question on a questionnaire.</summary>
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

public enum MatchKind
{
    /// <summary>The service is on the list and the rule is known.</summary>
    Listed,
    /// <summary>The code is on the list, but the modifiers sent (or their absence) do not settle which entry applies. A person must decide.</summary>
    Ambiguous,
    /// <summary>A well-formed code in a known system that is not on the list.</summary>
    NotListed,
    /// <summary>No code, an unknown code system, or a code that is not well formed. Nothing can be said about it.</summary>
    Unrecognised,
}

/// <summary>What the list says about a coded service.</summary>
/// <param name="Coding">The coding the answer is about.</param>
/// <param name="Requirement">The entry that applies, when the answer is <see cref="MatchKind.Listed"/>.</param>
/// <param name="Candidates">Every entry for the code, which is more than one when they differ by modifier.</param>
public sealed record ServiceMatch(MatchKind Kind, Coding? Coding, ServiceRequirement? Requirement, IReadOnlyList<ServiceRequirement> Candidates)
{
    /// <summary>
    /// The review a submitted request for this service gets. Anything the list cannot settle is pended for a person,
    /// never answered "not required" by guesswork.
    /// </summary>
    /// <returns>The review, or null when none is needed; and a note for the request's history.</returns>
    public (ReviewType? Review, string? Note) ReviewForRequest() => Kind switch
    {
        MatchKind.Listed => Requirement!.Rule switch
        {
            ReviewRule.PriorAuthorization => (ReviewType.PriorAuthorization, null),
            ReviewRule.ConcurrentReview => (ReviewType.ConcurrentReview, null),
            _ => (null, ServiceRequirement.MayNotRequire),
        },
        MatchKind.NotListed => (null, null),
        MatchKind.Ambiguous => (ReviewType.PriorAuthorization,
            $"The billing modifiers sent do not settle which service this is ({string.Join(", ", Candidates.Select(c => $"{c.Describe()} {c.Name}"))}); check which was meant."),
        _ => (ReviewType.PriorAuthorization, $"The service code '{Coding?.Code}' in '{Coding?.System}' was not recognised; check the code."),
    };
}

/// <summary>
/// The plan's list of services and questionnaires, loaded from service-requirements.json.
/// The list is checked when it is built, so a mistake in the file stops the service at start-up
/// rather than giving wrong answers later.
/// </summary>
public class ServiceRequirementCatalog
{
    private readonly List<ServiceRequirement> _services;
    private readonly Dictionary<string, QuestionnaireDefinition> _questionnaires;

    public ServiceRequirementCatalog(IEnumerable<ServiceRequirement> requirements, IEnumerable<QuestionnaireDefinition> questionnaires)
    {
        _services = requirements.ToList();
        var questionnaireList = questionnaires.ToList();
        var problems = Validate(_services, questionnaireList);
        if (problems.Count > 0)
            throw new InvalidOperationException("service-requirements.json is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        _questionnaires = questionnaireList.ToDictionary(q => q.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ServiceRequirement> All => _services;
    public IReadOnlyCollection<QuestionnaireDefinition> Questionnaires => _questionnaires.Values;

    public QuestionnaireDefinition? FindQuestionnaire(string? id) =>
        id is null ? null : _questionnaires.GetValueOrDefault(id.Trim());

    /// <summary>
    /// The one rule every entry point uses to decide what a coded service is.
    /// The first coding whose code is on the list wins; failing that, the first recognisable coding; failing that, the first coding.
    /// </summary>
    /// <param name="modifiers">The billing modifiers sent with the service, or null when the caller cannot send them (CRD orders carry none).</param>
    public ServiceMatch Match(IEnumerable<Coding>? codings, IReadOnlyCollection<string>? modifiers)
    {
        var list = codings?.Where(c => !string.IsNullOrWhiteSpace(c.Code)).ToList() ?? [];
        foreach (var coding in list)
        {
            var candidates = Candidates(coding.System, coding.Code);
            if (candidates.Count > 0) return Pick(coding, candidates, modifiers);
        }
        var recognised = list.FirstOrDefault(c => CodeSystems.IsRecognised(c.System, c.Code));
        return recognised is not null
            ? new ServiceMatch(MatchKind.NotListed, recognised, null, [])
            : new ServiceMatch(MatchKind.Unrecognised, list.FirstOrDefault(), null, []);
    }

    private List<ServiceRequirement> Candidates(string? system, string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return _services.Where(s => s.System == system && s.NormalizedCode == normalized).ToList();
    }

    private static bool SameMeaning(ServiceRequirement a, ServiceRequirement b) =>
        a.Rule == b.Rule && string.Equals(a.Questionnaire, b.Questionnaire, StringComparison.OrdinalIgnoreCase);

    private static ServiceMatch Pick(Coding coding, List<ServiceRequirement> candidates, IReadOnlyCollection<string>? modifiers)
    {
        ServiceMatch Listed(ServiceRequirement r) => new(MatchKind.Listed, coding, r, candidates);
        ServiceMatch Ambiguous() => new(MatchKind.Ambiguous, coding, null, candidates);
        var anyReviewed = candidates.Any(c => c.Rule != ReviewRule.NoPriorAuthorization);

        if (modifiers is null)
        {
            // Without modifiers the code alone must settle it: every entry for it has to lead to the same rule and questionnaire.
            return candidates.All(c => SameMeaning(c, candidates[0])) ? Listed(candidates[0]) : Ambiguous();
        }

        // With modifiers known, an entry applies when all its modifiers were sent; the most specific entries win.
        var sent = modifiers.Select(m => m.Trim().ToUpperInvariant()).ToHashSet();
        var fitting = candidates.Where(c => c.NormalizedModifiers.All(sent.Contains)).ToList();
        if (fitting.Count == 0)
        {
            // The code is listed but the modifiers it needs were not sent. If it could be a reviewed service, a person decides.
            return anyReviewed ? Ambiguous() : new ServiceMatch(MatchKind.NotListed, coding, null, candidates);
        }
        var most = fitting.Max(c => c.NormalizedModifiers.Length);
        var best = fitting.Where(c => c.NormalizedModifiers.Length == most).ToList();
        // Two equally specific entries both fit (U1 and U2 sent): the request names two services, so a person decides.
        if (best.Count > 1) return Ambiguous();

        // Falling back to an entry that needs no review while another entry for the code does (H0019 with no level
        // modifier) may just be a missing modifier, so a person decides rather than the request being waved through.
        return best[0].Rule == ReviewRule.NoPriorAuthorization && anyReviewed ? Ambiguous() : Listed(best[0]);
    }

    private static List<string> Validate(List<ServiceRequirement> services, List<QuestionnaireDefinition> questionnaires)
    {
        var problems = new List<string>();
        var questionnaireIds = questionnaires.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var s in services)
        {
            var name = string.IsNullOrWhiteSpace(s.Code) ? "(an entry with no code)" : s.Describe();
            if (string.IsNullOrWhiteSpace(s.Code)) problems.Add($"{name}: Code is empty.");
            else if (!CodeSystems.IsRecognised(s.System, s.Code)) problems.Add($"{name}: '{s.Code}' is not a well-formed code in {s.System}.");
            if (string.IsNullOrWhiteSpace(s.Name)) problems.Add($"{name}: Name is empty.");
            if (s.Modifiers.Any(m => string.IsNullOrWhiteSpace(m) || m.Trim().Length != 2)) problems.Add($"{name}: every modifier must be two characters.");
            if (s.Rule == ReviewRule.NoPriorAuthorization)
            {
                if (s.Questionnaire is not null) problems.Add($"{name}: a service that needs no review cannot have a Questionnaire.");
            }
            else if (string.IsNullOrWhiteSpace(s.Questionnaire)) problems.Add($"{name}: a service that is reviewed needs a Questionnaire.");
            else if (!questionnaireIds.Contains(s.Questionnaire)) problems.Add($"{name}: Questionnaire '{s.Questionnaire}' is not defined.");
        }
        foreach (var duplicate in services.GroupBy(s => (s.System, CodeWithModifiers: s.Describe())).Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Key.CodeWithModifiers} ({duplicate.Key.System}) is listed more than once.");

        foreach (var duplicate in questionnaires.GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Questionnaire '{duplicate.Key}' is defined more than once.");
        foreach (var q in questionnaires) problems.AddRange(ValidateQuestionnaire(q));
        return problems;
    }

    private static IEnumerable<string> ValidateQuestionnaire(QuestionnaireDefinition questionnaire)
    {
        var name = string.IsNullOrWhiteSpace(questionnaire.Id) ? "(a questionnaire with no id)" : questionnaire.Id;
        if (string.IsNullOrWhiteSpace(questionnaire.Id)) yield return $"{name}: Id is empty.";
        else if (!Regex.IsMatch(questionnaire.Id, "^[A-Za-z][A-Za-z0-9\\-.]{0,63}$")) yield return $"{name}: Id must start with a letter and may only use letters, digits, '-' and '.' (a FHIR id that also makes a valid FHIR name).";
        if (string.IsNullOrWhiteSpace(questionnaire.Title)) yield return $"{name}: Title is empty.";
        foreach (var duplicate in questionnaire.Questions.GroupBy(q => q.LinkId).Where(g => g.Count() > 1))
            yield return $"{name}: question linkId '{duplicate.Key}' is used more than once.";
        foreach (var q in questionnaire.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.LinkId)) yield return $"{name}: a question has no LinkId.";
            if (!RequirementQuestion.Types.Contains(q.Type, StringComparer.OrdinalIgnoreCase))
                yield return $"{name}/{q.LinkId}: Type '{q.Type}' is not one of {string.Join(", ", RequirementQuestion.Types)}.";
            var isChoice = q.Type.Equals("choice", StringComparison.OrdinalIgnoreCase);
            if (isChoice && q.Options.Length == 0) yield return $"{name}/{q.LinkId}: a choice question needs Options.";
            if (!isChoice && q.Options.Length > 0) yield return $"{name}/{q.LinkId}: only choice questions can have Options.";
            var codes = q.Options.Select(RequirementQuestion.OptionCode).ToList();
            if (codes.Any(string.IsNullOrEmpty)) yield return $"{name}/{q.LinkId}: an option has no letters or digits, so it has no code.";
            foreach (var duplicate in codes.GroupBy(c => c).Where(g => g.Count() > 1))
                yield return $"{name}/{q.LinkId}: two options share the code '{duplicate.Key}'. Reword one of them.";
        }
    }
}
