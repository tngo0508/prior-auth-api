using System.Text.Json;

namespace PriorAuth.Api.Crd;

/// <summary>
/// The parts of a CDS Hooks service call this service uses, read and checked once.
/// Every JSON kind is checked before it is read, so a body of the wrong shape gets a 422 with an
/// explanation (CRD: valid JSON that does not conform to CDS Hooks is a 422), never an exception.
/// </summary>
public sealed record CdsHookRequest(
    string Hook,
    string HookInstance,
    string DraftOrdersJson,
    IReadOnlySet<string>? Selections,
    string? PrefetchedCoverageId,
    IReadOnlyDictionary<string, JsonElement> Configuration)
{
    public const string ConfigurationExtension = "davinci-crd.configuration";

    /// <summary>Reads a parsed request body. Returns an error message when the body does not have the CDS Hooks shape.</summary>
    /// <param name="selectionsRequired">order-select: context.selections is required and limits which draft orders are answered.</param>
    public static (CdsHookRequest? Request, string? Error) Read(JsonElement root, string expectedHook, bool selectionsRequired)
    {
        if (root.ValueKind != JsonValueKind.Object) return Fail("The request body must be a JSON object.");

        if (!root.TryGetProperty("hook", out var hook) || hook.ValueKind != JsonValueKind.String || hook.GetString() != expectedHook)
            return Fail($"hook must be the string '{expectedHook}' for this service.");
        if (!root.TryGetProperty("hookInstance", out var instance) || instance.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(instance.GetString()))
            return Fail("hookInstance is required and must be a string.");

        if (!root.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object)
            return Fail("context is required and must be an object.");
        if (!context.TryGetProperty("draftOrders", out var draftOrders) || draftOrders.ValueKind != JsonValueKind.Object)
            return Fail("context.draftOrders is required and must be a FHIR Bundle object.");

        HashSet<string>? selections = null;
        if (selectionsRequired)
        {
            if (!context.TryGetProperty("selections", out var list) || list.ValueKind != JsonValueKind.Array)
                return Fail("context.selections is required for order-select and must be an array of references such as \"ServiceRequest/123\".");
            selections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return Fail("Each entry of context.selections must be a string reference such as \"ServiceRequest/123\".");
                selections.Add(item.GetString()!);
            }
        }

        var (coverageId, prefetchError) = PrefetchedCoverage(root);
        if (prefetchError is not null) return Fail(prefetchError);

        var (configuration, configurationError) = ReadConfiguration(root);
        if (configurationError is not null) return Fail(configurationError);

        return (new CdsHookRequest(hook.GetString()!, instance.GetString()!, draftOrders.GetRawText(), selections, coverageId, configuration), null);
    }

    /// <summary>
    /// The EHR may send the member's coverage ahead of time, as a Coverage or a search result Bundle.
    /// Absent or null means the EHR had nothing to send, which is normal. A value of the wrong kind is an error.
    /// </summary>
    private static (string? CoverageId, string? Error) PrefetchedCoverage(JsonElement root)
    {
        if (!root.TryGetProperty("prefetch", out var prefetch) || prefetch.ValueKind == JsonValueKind.Null) return (null, null);
        if (prefetch.ValueKind != JsonValueKind.Object) return (null, "prefetch must be an object.");
        if (!prefetch.TryGetProperty("coverage", out var coverage) || coverage.ValueKind == JsonValueKind.Null) return (null, null);
        if (coverage.ValueKind != JsonValueKind.Object) return (null, "prefetch.coverage must be a FHIR resource object or null.");

        var type = StringProperty(coverage, "resourceType");
        if (type.Error is not null) return (null, "prefetch.coverage.resourceType must be a string.");

        if (type.Value == "Bundle")
        {
            if (!coverage.TryGetProperty("entry", out var entries)) return (null, null);
            if (entries.ValueKind != JsonValueKind.Array) return (null, "prefetch.coverage.entry must be an array.");
            JsonElement? first = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return (null, "Each prefetch.coverage.entry must be an object.");
                if (!entry.TryGetProperty("resource", out var resource)) continue;
                if (resource.ValueKind != JsonValueKind.Object) return (null, "prefetch.coverage.entry.resource must be an object.");
                first ??= resource;
            }
            if (first is null) return (null, null);
            coverage = first.Value;
            type = StringProperty(coverage, "resourceType");
            if (type.Error is not null) return (null, "prefetch.coverage.entry.resource.resourceType must be a string.");
        }

        if (type.Value != "Coverage") return (null, null);
        var id = StringProperty(coverage, "id");
        if (id.Error is not null) return (null, "The prefetched Coverage id must be a string.");
        return (string.IsNullOrWhiteSpace(id.Value) ? null : id.Value, null);
    }

    /// <summary>
    /// CRD hook configuration: extension."davinci-crd.configuration" is an object of option code to value.
    /// Absent configuration is normal. Options this service does not support are ignored by the caller.
    /// </summary>
    private static (IReadOnlyDictionary<string, JsonElement> Options, string? Error) ReadConfiguration(JsonElement root)
    {
        var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("extension", out var extension) || extension.ValueKind == JsonValueKind.Null) return (options, null);
        if (extension.ValueKind != JsonValueKind.Object) return (options, "extension must be an object.");
        if (!extension.TryGetProperty(ConfigurationExtension, out var configuration) || configuration.ValueKind == JsonValueKind.Null) return (options, null);
        if (configuration.ValueKind != JsonValueKind.Object) return (options, $"extension.{ConfigurationExtension} must be an object.");
        foreach (var option in configuration.EnumerateObject()) options[option.Name] = option.Value.Clone();
        return (options, null);
    }

    private static (string? Value, string? Error) StringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return (null, null);
        return value.ValueKind == JsonValueKind.String ? (value.GetString(), null) : (null, $"{name} must be a string.");
    }

    private static (CdsHookRequest?, string?) Fail(string message) => (null, message);
}
