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

    /// <summary>Parses a Bundle. Returns an error message when the JSON is not valid FHIR.</summary>
    public (Bundle? Bundle, string? Error) ReadBundle(string json) => Read<Bundle>(json);

    /// <summary>Parses one FHIR resource of a known type. Returns an error message when the JSON is not valid FHIR.</summary>
    public (T? Resource, string? Error) Read<T>(string json) where T : Resource
    {
        var name = typeof(T).Name;
        try
        {
            var resource = JsonSerializer.Deserialize<T>(json, _options);
            return resource is null ? (null, $"The request body must be a FHIR {name}.") : (resource, null);
        }
        catch (Exception ex) when (ex is DeserializationFailedException or JsonException)
        {
            return (null, $"The request body is not a valid FHIR {name}: {ex.Message}");
        }
    }

    public IResult Result(Resource resource, int status = StatusCodes.Status200OK) =>
        Results.Text(Write(resource), "application/fhir+json", Encoding.UTF8, status);

    public IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Result(new OperationOutcome
    {
        Issue =
        [
            new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = status == StatusCodes.Status404NotFound ? OperationOutcome.IssueType.NotFound : OperationOutcome.IssueType.Invalid,
                Diagnostics = message,
            },
        ],
    }, status);
}
