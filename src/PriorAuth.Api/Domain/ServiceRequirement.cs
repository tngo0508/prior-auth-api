namespace PriorAuth.Api.Domain;

/// <summary>A covered service that needs prior authorization, with the documentation the plan requires.</summary>
public class ServiceRequirement
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The Medi-Cal program the service belongs to, for example SMHS or DMC-ODS.</summary>
    public string Program { get; init; } = "";
    public string[] Documentation { get; init; } = [];
}

/// <summary>The list of service requirements, loaded from service-requirements.json.</summary>
public class ServiceRequirementCatalog(IEnumerable<ServiceRequirement> requirements)
{
    private readonly Dictionary<string, ServiceRequirement> _byCode =
        requirements.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ServiceRequirement> All => _byCode.Values;

    public ServiceRequirement? Find(string code) => _byCode.GetValueOrDefault(code);
}
