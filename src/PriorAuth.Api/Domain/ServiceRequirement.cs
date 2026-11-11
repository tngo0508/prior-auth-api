namespace PriorAuth.Api.Domain;

/// <summary>A covered service that needs prior authorization, with the documentation the plan requires.</summary>
public class ServiceRequirement
{
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
    public string LinkId { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>boolean, string, text, date or choice.</summary>
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    /// <summary>The allowed answers when Type is choice.</summary>
    public string[] Options { get; init; } = [];
}

/// <summary>The list of service requirements, loaded from service-requirements.json.</summary>
public class ServiceRequirementCatalog(IEnumerable<ServiceRequirement> requirements)
{
    private readonly Dictionary<string, ServiceRequirement> _byCode =
        requirements.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ServiceRequirement> All => _byCode.Values;

    public ServiceRequirement? Find(string? code) => code is null ? null : _byCode.GetValueOrDefault(code);

    public ServiceRequirement? FindByQuestionnaireId(string id) =>
        _byCode.Values.FirstOrDefault(r => r.QuestionnaireId.Equals(id, StringComparison.OrdinalIgnoreCase));
}
