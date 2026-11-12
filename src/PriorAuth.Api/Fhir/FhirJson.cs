using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace PriorAuth.Api.Fhir;

/// <summary>Reads and writes FHIR R4 JSON using the Firely SDK.</summary>
public class FhirJson
{
    private readonly JsonSerializerOptions _options = new JsonSerializerOptions { WriteIndented = true }.ForFhir(ModelInfo.ModelInspector);

    public string Write(Resource resource) => JsonSerializer.Serialize(resource, resource.GetType(), _options);

    /// <summary>Parses a Bundle. Returns an error message when the JSON is not valid FHIR or is another resource type.</summary>
    public (Bundle? Bundle, string? Error) ReadBundle(string json) => Read<Bundle>(json);

    /// <summary>
    /// Parses the Bundle an operation such as $submit takes. FHIR allows an operation's single resource input
    /// to be sent either directly or wrapped in Parameters under the name "resource"; both are accepted.
    /// </summary>
    public (Bundle? Bundle, string? Error) ReadOperationBundle(string json)
    {
        var (resource, error) = Read<Resource>(json);
        if (error is not null) return (null, error);
        return resource switch
        {
            Bundle bundle => (bundle, null),
            Parameters parameters when parameters.Parameter.FirstOrDefault(p => p.Name == "resource")?.Resource is Bundle wrapped => (wrapped, null),
            Parameters => (null, "The Parameters resource must carry the Bundle in a parameter named 'resource'."),
            _ => (null, $"The request body must be a FHIR Bundle, not a {resource!.TypeName}."),
        };
    }

    /// <summary>Parses one FHIR resource of a known type. Returns an error message when the JSON is not valid FHIR or is another type.</summary>
    public (T? Resource, string? Error) Read<T>(string json) where T : Resource
    {
        var name = typeof(T) == typeof(Resource) ? "resource" : typeof(T).Name;
        try
        {
            var resource = JsonSerializer.Deserialize<Resource>(json, _options);
            return resource switch
            {
                null => (null, $"The request body must be a FHIR {name}."),
                T typed => (typed, null),
                _ => (null, $"The request body must be a FHIR {name}, not a {resource.TypeName}."),
            };
        }
        catch (Exception ex) when (ex is DeserializationFailedException or JsonException or InvalidCastException or NotSupportedException)
        {
            return (null, $"The request body is not a valid FHIR {name}: {ex.Message}");
        }
    }

    public IResult Result(Resource resource, int status = StatusCodes.Status200OK) =>
        Results.Text(Write(resource), "application/fhir+json", Encoding.UTF8, status);

    public IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Result(Outcome(message, status), status);

    public static OperationOutcome Outcome(string message, int status) => new()
    {
        Issue =
        [
            new OperationOutcome.IssueComponent
            {
                Severity = status >= 500 ? OperationOutcome.IssueSeverity.Fatal : OperationOutcome.IssueSeverity.Error,
                Code = status switch
                {
                    StatusCodes.Status401Unauthorized => OperationOutcome.IssueType.Login,
                    StatusCodes.Status403Forbidden => OperationOutcome.IssueType.Forbidden,
                    StatusCodes.Status404NotFound => OperationOutcome.IssueType.NotFound,
                    StatusCodes.Status405MethodNotAllowed or StatusCodes.Status406NotAcceptable or StatusCodes.Status415UnsupportedMediaType => OperationOutcome.IssueType.NotSupported,
                    StatusCodes.Status409Conflict => OperationOutcome.IssueType.Conflict,
                    StatusCodes.Status413PayloadTooLarge => OperationOutcome.IssueType.TooCostly,
                    StatusCodes.Status422UnprocessableEntity => OperationOutcome.IssueType.BusinessRule,
                    >= 500 => OperationOutcome.IssueType.Exception,
                    _ => OperationOutcome.IssueType.Invalid,
                },
                Diagnostics = message,
            },
        ],
    };
}
