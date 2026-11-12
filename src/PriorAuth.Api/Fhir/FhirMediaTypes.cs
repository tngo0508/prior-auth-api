namespace PriorAuth.Api.Fhir;

/// <summary>Which media types and _format values mean FHIR JSON.</summary>
public static class FhirMediaTypes
{
    private static readonly string[] Json = ["application/fhir+json", "application/json", "json", "application/json+fhir"];

    /// <summary>True for a JSON media type or _format value. Parameters such as charset are ignored; fhirVersion must be 4.0.</summary>
    public static bool IsJson(string mediaType)
    {
        var parts = mediaType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Json.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return false;
        return parts.Skip(1).All(p => !p.StartsWith("fhirVersion=", StringComparison.OrdinalIgnoreCase) || p["fhirVersion=".Length..].StartsWith("4.0"));
    }

    /// <summary>True when an Accept header allows a FHIR JSON answer.</summary>
    public static bool AcceptsJson(string accept) =>
        accept.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(type =>
        {
            var name = type.Split(';')[0].Trim();
            return name is "*/*" or "application/*" || IsJson(type);
        });
}
