using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PriorAuth.Api.Tests;

/// <summary>Runs the real application against a throwaway SQLite database file.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"priorauth-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:PriorAuth", $"Data Source={_databasePath};Pooling=False");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}

public class PriorAuthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // A signed-in provider system. Each test gets its own, so tests cannot see each other's requests.
    private HttpClient _client = null!;

    public async Task InitializeAsync() =>
        _client = await (await TestProvider.RegisterAsync(factory.CreateClient())).SignInAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Requirements list ----------

    [Fact]
    public async Task Requirements_list_states_the_documentation_for_a_service_that_needs_authorization()
    {
        var list = await _client.GetFromJsonAsync<JsonElement>("/requirements?code=H2019");

        var requirement = Assert.Single(list.EnumerateArray());
        Assert.Equal("Therapeutic Behavioral Services", requirement.GetProperty("name").GetString());
        Assert.NotEmpty(requirement.GetProperty("documentation").EnumerateArray());
    }

    [Fact]
    public async Task Requirements_list_is_empty_for_a_service_that_needs_no_authorization()
    {
        var list = await _client.GetFromJsonAsync<JsonElement>("/requirements?code=90834");

        Assert.Empty(list.EnumerateArray());
    }

    // ---------- Submitting ----------

    [Fact]
    public async Task A_submitted_request_is_pended_for_a_reviewer()
    {
        var response = await Submit(NewRequestId(), "H2019");

        Assert.Equal("queued", response.GetProperty("outcome").GetString());
        Assert.Equal("A4", ReviewCode(response));
    }

    [Fact]
    public async Task A_request_for_a_service_not_on_the_list_is_answered_as_not_required()
    {
        var response = await Submit(NewRequestId(), "90834");

        Assert.Equal("complete", response.GetProperty("outcome").GetString());
        Assert.Equal("NA", ReviewCode(response));
    }

    [Fact]
    public async Task Resending_the_same_request_does_not_create_a_second_one()
    {
        var requestId = NewRequestId();

        var first = await Submit(requestId, "H2019");
        var second = await Submit(requestId, "H2019");

        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_valid_FHIR_is_rejected_with_an_OperationOutcome()
    {
        var response = await _client.PostAsync("/fhir/Claim/$submit", Fhir("""{"resourceType":"Bundle","type":"nonsense"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OperationOutcome", body.GetProperty("resourceType").GetString());
    }

    [Fact]
    public async Task A_claim_that_is_not_a_preauthorization_is_rejected()
    {
        var response = await _client.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle(NewRequestId(), "H2019", use: "claim")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Decisions ----------

    [Fact]
    public async Task An_approval_is_returned_with_an_end_date_and_an_authorization_number()
    {
        var requestId = NewRequestId();
        var id = (await Submit(requestId, "H2019")).GetProperty("id").GetString()!;
        var through = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90);

        var decision = await _client.PostAsJsonAsync($"/internal/requests/{id}/approve", new { through, reviewer = "Test Reviewer" });
        var status = await Inquire(requestId);

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal("complete", status.GetProperty("outcome").GetString());
        Assert.Equal("A1", ReviewCode(status));
        Assert.Equal(through.ToString("yyyy-MM-dd"), status.GetProperty("preAuthPeriod").GetProperty("end").GetString());
        Assert.StartsWith("PA-", status.GetProperty("preAuthRef").GetString());
    }

    [Fact]
    public async Task An_approval_cannot_end_in_the_past()
    {
        var id = (await Submit(NewRequestId(), "H2019")).GetProperty("id").GetString()!;
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        var decision = await _client.PostAsJsonAsync($"/internal/requests/{id}/approve", new { through = yesterday, reviewer = "Test Reviewer" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, decision.StatusCode);
    }

    [Fact]
    public async Task A_denial_without_a_reason_is_refused_and_the_request_stays_pended()
    {
        var requestId = NewRequestId();
        var id = (await Submit(requestId, "H2019")).GetProperty("id").GetString()!;

        var decision = await _client.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reason = "  ", reviewer = "Test Reviewer" });
        var status = await Inquire(requestId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, decision.StatusCode);
        Assert.Equal("A4", ReviewCode(status));
    }

    [Fact]
    public async Task A_denial_is_returned_with_its_specific_reason()
    {
        var requestId = NewRequestId();
        var id = (await Submit(requestId, "H2019")).GetProperty("id").GetString()!;

        await _client.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reason = "Assessment is older than 12 months", reviewer = "Test Reviewer" });
        var status = await Inquire(requestId);

        Assert.Equal("A3", ReviewCode(status));
        Assert.Contains("Assessment is older than 12 months", status.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task An_information_request_keeps_the_request_open_and_tells_the_provider_what_is_missing()
    {
        var requestId = NewRequestId();
        var id = (await Submit(requestId, "H2019")).GetProperty("id").GetString()!;

        await _client.PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan", reviewer = "Test Reviewer" });
        var status = await Inquire(requestId);
        var laterApproval = await _client.PostAsJsonAsync($"/internal/requests/{id}/approve",
            new { through = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), reviewer = "Test Reviewer" });

        Assert.Equal("queued", status.GetProperty("outcome").GetString());
        Assert.Contains("Client plan", status.GetProperty("disposition").GetString());
        Assert.Equal(HttpStatusCode.OK, laterApproval.StatusCode);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var id = (await Submit(NewRequestId(), "H2019")).GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reason = "Not medically necessary per assessment", reviewer = "Test Reviewer" });

        var second = await _client.PostAsJsonAsync($"/internal/requests/{id}/approve",
            new { through = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), reviewer = "Test Reviewer" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Every_step_is_recorded_in_the_history_of_the_request()
    {
        var requestId = NewRequestId();
        var id = (await Submit(requestId, "H2019")).GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan", reviewer = "Reviewer A" });
        await _client.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reason = "Client plan never supplied", reviewer = "Reviewer B" });

        var queue = await _client.GetFromJsonAsync<JsonElement>("/internal/requests");
        var request = queue.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

        var actors = request.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("actor").GetString()!).ToArray();
        Assert.Equal(["Example Clinic (synthetic)", "Reviewer A", "Reviewer B"], actors);
        Assert.Equal("Reviewer B", request.GetProperty("decidedBy").GetString());
    }

    // ---------- Reading ----------

    [Fact]
    public async Task A_response_can_be_read_by_its_id()
    {
        var id = (await Submit(NewRequestId(), "H2019")).GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/fhir/ClaimResponse/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Inquiring_about_an_unknown_request_returns_not_found()
    {
        var response = await _client.PostAsync("/fhir/Claim/$inquire", Fhir(RequestBundle("does-not-exist", "H2019")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_capability_statement_advertises_submit_and_inquire()
    {
        var capability = await _client.GetStringAsync("/fhir/metadata");

        Assert.Contains("Claim-submit", capability);
        Assert.Contains("Claim-inquiry", capability);
    }

    // ---------- Helpers ----------

    private static string NewRequestId() => $"REQ-{Guid.NewGuid():N}";

    private static StringContent Fhir(string json) => new(json, Encoding.UTF8, "application/fhir+json");

    private async Task<JsonElement> Submit(string requestId, string serviceCode) =>
        await ClaimResponseFrom(await _client.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle(requestId, serviceCode))));

    private async Task<JsonElement> Inquire(string requestId) =>
        await ClaimResponseFrom(await _client.PostAsync("/fhir/Claim/$inquire", Fhir(RequestBundle(requestId, "H2019"))));

    private static async Task<JsonElement> ClaimResponseFrom(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("entry")[0].GetProperty("resource");
    }

    private static string? ReviewCode(JsonElement claimResponse) =>
        claimResponse.GetProperty("item")[0].GetProperty("adjudication")[0].GetProperty("extension")[0]
            .GetProperty("extension")[0].GetProperty("valueCodeableConcept").GetProperty("coding")[0].GetProperty("code").GetString();

    private static string RequestBundle(string requestId, string serviceCode, string use = "preauthorization") => $$"""
        {
          "resourceType": "Bundle",
          "type": "collection",
          "entry": [
            {
              "fullUrl": "urn:uuid:11111111-1111-1111-1111-111111111111",
              "resource": {
                "resourceType": "Claim",
                "identifier": [{ "system": "http://example.org/provider/claim-id", "value": "{{requestId}}" }],
                "status": "active",
                "type": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/claim-type", "code": "professional" }] },
                "use": "{{use}}",
                "patient": { "reference": "urn:uuid:22222222-2222-2222-2222-222222222222" },
                "created": "2026-11-11",
                "insurer": { "display": "County Behavioral Health Plan (synthetic)" },
                "provider": { "display": "Example Clinic (synthetic)" },
                "priority": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/processpriority", "code": "normal" }] },
                "insurance": [{ "sequence": 1, "focal": true, "coverage": { "display": "Synthetic coverage" } }],
                "item": [{ "sequence": 1, "productOrService": { "coding": [{ "code": "{{serviceCode}}" }] } }]
              }
            },
            {
              "fullUrl": "urn:uuid:22222222-2222-2222-2222-222222222222",
              "resource": { "resourceType": "Patient", "name": [{ "family": "Testpatient", "given": ["Alex"] }] }
            }
          ]
        }
        """;
}
