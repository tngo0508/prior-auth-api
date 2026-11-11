// =====================================================================================
// PROTOTYPE - THROWAWAY CODE. Not production. Synthetic data only, never real PHI.
//
// Question this prototype answers:
//   "Does a thin FHIR facade, where every request pends for a human reviewer and the
//    provider polls for the decision, cover the Prior Authorization API flow?"
//
// Deliberately missing: authentication (SMART Backend Services), persistence, validation
// against the Da Vinci PAS profiles, audit logging, error handling, tests.
// =====================================================================================

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fhirJson = new JsonSerializerOptions { WriteIndented = true }.ForFhir(ModelInfo.ModelInspector);
var store = new ConcurrentDictionary<string, AuthRequest>();
var nextNumber = 1000;

// Placeholder list. The real list and documentation rules must come from the agency's
// utilization management policy, not from this file.
var requirements = new[]
{
    new ServiceRequirement("H2019", "Therapeutic Behavioral Services", "SMHS", "Prior",
        ["Current assessment", "Client plan showing TBS need", "Target behaviours and transition plan"]),
    new ServiceRequirement("H2012", "Day Treatment Intensive / Day Rehabilitation", "SMHS", "Prior",
        ["Current assessment", "Proposed schedule and hours per day", "Reason lower level of care is insufficient"]),
    new ServiceRequirement("S5145", "Therapeutic Foster Care", "SMHS", "Prior",
        ["Child and Family Team recommendation", "Current assessment"]),
    new ServiceRequirement("H0019", "Residential Treatment", "DMC-ODS", "Prior",
        ["ASAM level of care assessment", "Diagnosis", "Requested length of stay"]),
};

// ---------- FHIR surface (what a provider's EHR calls) --------------------------------

app.MapGet("/fhir/metadata", () => Fhir(new CapabilityStatement
{
    Status = PublicationStatus.Active,
    Date = "2026-11-11",
    Kind = CapabilityStatementKind.Instance,
    FhirVersion = FHIRVersion.N4_0_1,
    Format = ["json"],
    Rest =
    [
        new CapabilityStatement.RestComponent
        {
            Mode = CapabilityStatement.RestfulCapabilityMode.Server,
            Resource =
            [
                new CapabilityStatement.ResourceComponent
                {
                    Type = "Claim",
                    Operation =
                    [
                        new() { Name = "submit", Definition = "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit" },
                        new() { Name = "inquire", Definition = "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquiry" },
                    ],
                },
                new CapabilityStatement.ResourceComponent
                {
                    Type = "ClaimResponse",
                    Interaction = [new() { Code = CapabilityStatement.TypeRestfulInteraction.Read }],
                },
            ],
        },
    ],
}));

// Which services need authorization, and what documentation each needs.
app.MapGet("/requirements", (string? code) =>
    Results.Json(requirements.Where(r => code is null || r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))));

// Submit a prior authorization request (Da Vinci PAS Claim/$submit).
app.MapPost("/fhir/Claim/$submit", async (HttpRequest http) =>
{
    var (bundle, error) = await ReadBundle(http);
    if (error is not null) return error;

    var claim = bundle!.Entry.Select(e => e.Resource).OfType<Claim>().FirstOrDefault();
    if (claim is null) return Problem("The Bundle must contain a Claim.");
    if (claim.Use != ClaimUseCode.Preauthorization) return Problem("Claim.use must be 'preauthorization'.");

    var coding = claim.Item.FirstOrDefault()?.ProductOrService?.Coding.FirstOrDefault();
    if (coding?.Code is null) return Problem("Claim.item[0].productOrService must carry a code.");

    var requirement = requirements.FirstOrDefault(r => r.Code == coding.Code);
    var patient = bundle.Entry.Select(e => e.Resource).OfType<Patient>().FirstOrDefault();
    var id = Guid.NewGuid().ToString("N")[..12];

    var request = new AuthRequest
    {
        Id = id,
        Claim = claim,
        ClaimIdentifier = claim.Identifier.FirstOrDefault()?.Value ?? id,
        PatientName = patient?.Name.FirstOrDefault()?.ToString() ?? claim.Patient?.Display ?? "(unknown)",
        ProviderName = claim.Provider?.Display ?? claim.Provider?.Reference ?? "(unknown)",
        ServiceCode = coding.Code,
        ServiceName = requirement?.Name ?? coding.Display ?? coding.Code,
        ReceivedAt = DateTimeOffset.Now,
        Status = requirement is null ? AuthStatus.NotRequired : AuthStatus.Pended,
    };
    request.Log(requirement is null ? "Received. Service is not on the requirements list." : "Received. Pended for review.");
    store[id] = request;

    return Fhir(ResponseBundle(request));
});

// Ask for the current state of a request (Da Vinci PAS Claim/$inquire), matched on Claim.identifier.
app.MapPost("/fhir/Claim/$inquire", async (HttpRequest http) =>
{
    var (bundle, error) = await ReadBundle(http);
    if (error is not null) return error;

    var identifier = bundle!.Entry.Select(e => e.Resource).OfType<Claim>().FirstOrDefault()?.Identifier.FirstOrDefault()?.Value;
    var request = store.Values.FirstOrDefault(r => r.ClaimIdentifier == identifier);
    return request is null ? Problem($"No request found for Claim.identifier '{identifier}'.", 404) : Fhir(ResponseBundle(request));
});

// Poll a single response by id.
app.MapGet("/fhir/ClaimResponse/{id}", (string id) =>
    store.TryGetValue(id, out var request) ? Fhir(ToClaimResponse(request)) : Problem($"ClaimResponse/{id} not found.", 404));

// ---------- Reviewer side (stands in for the existing authorization system) -----------

app.MapGet("/", () => Results.Content(ReviewerPage(), "text/html"));

app.MapPost("/reviewer/{id}/decide", async (string id, HttpRequest http) =>
{
    if (!store.TryGetValue(id, out var request)) return Results.NotFound();
    var form = await http.ReadFormAsync();
    var action = form["action"].ToString();
    var text = form["text"].ToString().Trim();

    switch (action)
    {
        case "approve" when DateOnly.TryParse(form["through"], out var through):
            request.Status = AuthStatus.Approved;
            request.AuthorizedThrough = through;
            request.AuthNumber = $"AUTH-{Interlocked.Increment(ref nextNumber)}";
            request.Reason = null;
            request.Log($"Approved through {through:yyyy-MM-dd} as {request.AuthNumber}.");
            break;
        case "deny" when text.Length > 0: // the rule requires a specific reason on every denial
            request.Status = AuthStatus.Denied;
            request.Reason = text;
            request.Log($"Denied: {text}");
            break;
        case "info" when text.Length > 0:
            request.Status = AuthStatus.InfoRequested;
            request.Reason = text;
            request.Log($"More information requested: {text}");
            break;
        default:
            request.Log($"Rejected reviewer action '{action}': an approval needs an end date, a denial or information request needs text.");
            break;
    }
    return Results.Redirect("/");
});

// Full in-memory state, for inspection.
app.MapGet("/prototype/state", () => Results.Json(store.Values.OrderBy(r => r.ReceivedAt).Select(r => new
{
    r.Id, r.ClaimIdentifier, r.PatientName, r.ProviderName, r.ServiceCode, r.ServiceName,
    Status = r.Status.ToString(), r.AuthNumber, r.AuthorizedThrough, r.Reason, r.ReceivedAt, r.History,
})));

app.Run();

// ---------- Helpers -------------------------------------------------------------------

IResult Fhir(Resource resource, int status = 200) =>
    Results.Text(JsonSerializer.Serialize(resource, resource.GetType(), fhirJson), "application/fhir+json", Encoding.UTF8, status);

IResult Problem(string message, int status = 400) => Fhir(new OperationOutcome
{
    Issue =
    [
        new OperationOutcome.IssueComponent
        {
            Severity = OperationOutcome.IssueSeverity.Error,
            Code = OperationOutcome.IssueType.Invalid,
            Diagnostics = message,
        },
    ],
}, status);

async Task<(Bundle? Bundle, IResult? Error)> ReadBundle(HttpRequest http)
{
    try
    {
        var bundle = await JsonSerializer.DeserializeAsync<Bundle>(http.Body, fhirJson);
        return bundle is null ? (null, Problem("Request body must be a FHIR Bundle.")) : (bundle, null);
    }
    catch (Exception ex) when (ex is DeserializationFailedException or JsonException)
    {
        return (null, Problem($"Could not parse the request as a FHIR Bundle: {ex.Message}"));
    }
}

Bundle ResponseBundle(AuthRequest request) => new()
{
    Type = Bundle.BundleType.Collection,
    Timestamp = DateTimeOffset.Now,
    Entry = [new Bundle.EntryComponent { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = ToClaimResponse(request) }],
};

// NOTE: the reviewAction extension below follows my reading of Da Vinci PAS 2.0.1 and has
// not been validated against the published profiles. Check it before relying on it.
ClaimResponse ToClaimResponse(AuthRequest request)
{
    var (outcome, code, display) = request.Status switch
    {
        AuthStatus.Approved => (ClaimProcessingCodes.Complete, "A1", "Certified in total"),
        AuthStatus.Denied => (ClaimProcessingCodes.Complete, "A3", "Not Certified"),
        AuthStatus.NotRequired => (ClaimProcessingCodes.Complete, "NA", "No Action Required"),
        _ => (ClaimProcessingCodes.Queued, "A4", "Pended"),
    };

    var reviewAction = new Extension { Url = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction" };
    reviewAction.Extension.Add(new Extension(
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode",
        new CodeableConcept("https://codesystem.x12.org/005010/306", code, display)));
    if (request.AuthNumber is not null)
        reviewAction.Extension.Add(new Extension("number", new FhirString(request.AuthNumber)));

    var adjudication = new ClaimResponse.AdjudicationComponent
    {
        Category = new CodeableConcept("http://terminology.hl7.org/CodeSystem/adjudication", "submitted"),
    };
    adjudication.Extension.Add(reviewAction);

    return new ClaimResponse
    {
        Id = request.Id,
        Status = FinancialResourceStatusCodes.Active,
        Type = request.Claim.Type,
        Use = ClaimUseCode.Preauthorization,
        Patient = request.Claim.Patient,
        CreatedElement = new FhirDateTime(DateTimeOffset.Now),
        Insurer = request.Claim.Insurer,
        Requestor = request.Claim.Provider,
        Request = new ResourceReference { Identifier = request.Claim.Identifier.FirstOrDefault() },
        Outcome = outcome,
        Disposition = request.Status switch
        {
            AuthStatus.Approved => $"Approved through {request.AuthorizedThrough:yyyy-MM-dd}.",
            AuthStatus.Denied => $"Denied: {request.Reason}",
            AuthStatus.InfoRequested => $"More information needed: {request.Reason}",
            AuthStatus.NotRequired => "Prior authorization is not required for this service.",
            _ => "Pended for clinical review.",
        },
        PreAuthRef = request.AuthNumber,
        PreAuthPeriod = request.AuthorizedThrough is { } through ? new Period { End = through.ToString("yyyy-MM-dd") } : null,
        Item = [new ClaimResponse.ItemComponent { ItemSequence = 1, Adjudication = [adjudication] }],
    };
}

string ReviewerPage()
{
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    var html = new StringBuilder("""
        <!doctype html><meta charset="utf-8"><title>PROTOTYPE reviewer queue</title>
        <style>
          body{font:14px system-ui;margin:24px;max-width:1100px}
          table{border-collapse:collapse;width:100%} td,th{border:1px solid #ccc;padding:6px 8px;text-align:left;vertical-align:top}
          .banner{background:#fff3cd;border:1px solid #e0c36a;padding:8px 12px;margin-bottom:16px}
          form{display:inline-block;margin:2px 8px 2px 0} small{color:#555}
        </style>
        <div class="banner"><b>PROTOTYPE.</b> Stands in for the existing authorization system. In-memory, synthetic data only. Refresh to see new requests.</div>
        <h1>Reviewer queue</h1>
        <table><tr><th>Request</th><th>Service</th><th>Status</th><th>Decide</th><th>History</th></tr>
        """);

    foreach (var r in store.Values.OrderBy(r => r.ReceivedAt))
    {
        var open = r.Status is AuthStatus.Pended or AuthStatus.InfoRequested;
        html.Append($"""
            <tr><td><b>{E(r.ClaimIdentifier)}</b><br>{E(r.PatientName)}<br><small>{E(r.ProviderName)}<br>ClaimResponse/{E(r.Id)}</small></td>
            <td>{E(r.ServiceCode)}<br>{E(r.ServiceName)}</td>
            <td><b>{r.Status}</b><br>{E(r.AuthNumber)} {E(r.Reason)}</td><td>
            """);
        if (open)
            html.Append($"""
                <form method="post" action="/reviewer/{E(r.Id)}/decide"><input type="hidden" name="action" value="approve">
                  through <input type="date" name="through" required> <button>Approve</button></form><br>
                <form method="post" action="/reviewer/{E(r.Id)}/decide"><input type="hidden" name="action" value="deny">
                  <input name="text" placeholder="specific denial reason" required> <button>Deny</button></form><br>
                <form method="post" action="/reviewer/{E(r.Id)}/decide"><input type="hidden" name="action" value="info">
                  <input name="text" placeholder="what is missing" required> <button>Request info</button></form>
                """);
        html.Append("</td><td><small>").Append(string.Join("<br>", r.History.Select(E))).Append("</small></td></tr>");
    }
    if (store.IsEmpty) html.Append("<tr><td colspan=5>No requests yet. Send requests.http or sample-pas-request.json to /fhir/Claim/$submit.</td></tr>");
    return html.Append("</table>").ToString();
}

// ---------- Types ---------------------------------------------------------------------

enum AuthStatus { Pended, InfoRequested, Approved, Denied, NotRequired }

record ServiceRequirement(string Code, string Name, string Program, string AuthorizationType, string[] Documentation);

class AuthRequest
{
    public required string Id { get; init; }
    public required Claim Claim { get; init; }
    public required string ClaimIdentifier { get; init; }
    public required string PatientName { get; init; }
    public required string ProviderName { get; init; }
    public required string ServiceCode { get; init; }
    public required string ServiceName { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public AuthStatus Status { get; set; }
    public string? AuthNumber { get; set; }
    public DateOnly? AuthorizedThrough { get; set; }
    public string? Reason { get; set; }
    public List<string> History { get; } = [];
    public void Log(string entry) => History.Add($"{DateTimeOffset.Now:HH:mm:ss} {entry}");
}
