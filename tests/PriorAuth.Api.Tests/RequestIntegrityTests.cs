using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PriorAuth.Api.Data;

namespace PriorAuth.Api.Tests;

/// <summary>
/// Requests are read, stored and answered faithfully: nothing in a request is silently dropped, an answer always describes
/// what was reviewed, and two people acting at once cannot leave a request in a contradictory state.
/// </summary>
public class RequestIntegrityTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private HttpClient _provider = null!;
    private HttpClient _staff = null!;
    private TestProvider _registration = null!;

    public async Task InitializeAsync()
    {
        _staff = factory.CreateClient();
        _registration = await TestProvider.RegisterAsync(_staff);
        _provider = await _registration.SignInAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Nothing is silently dropped ----------

    [Fact]
    public async Task A_request_for_two_services_is_refused_rather_than_half_reviewed()
    {
        var items = """
            [{ "sequence": 1, "productOrService": { "coding": [{ "system": "http://www.ama-assn.org/go/cpt", "code": "90834" }] } },
             { "sequence": 2, "productOrService": { "coding": [{ "system": "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "code": "H2019" }] } }]
            """;

        var response = await Post("/fhir/Claim/$submit", PasFixtures.RequestBundle(NewId(), items: items));

        await AssertRefused(response, HttpStatusCode.BadRequest, "one service per request");
    }

    [Fact]
    public async Task The_answer_names_the_line_the_provider_numbered()
    {
        var response = await Submit(PasFixtures.RequestBundle(NewId(), items: PasFixtures.Item(7, "H2019")));

        Assert.Equal(7, ClaimResponse(response).GetProperty("item")[0].GetProperty("itemSequence").GetInt32());
    }

    [Theory]
    [InlineData("no code system", "\"system\": \"http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets\", ", "", "code system")]
    [InlineData("cancelled claim", "\"status\": \"active\"", "\"status\": \"cancelled\"", "Claim.status")]
    [InlineData("patient not in the Bundle", "\"reference\": \"urn:uuid:22222222", "\"reference\": \"urn:uuid:99999999", "Claim.patient")]
    [InlineData("identifier with a trailing space", "\"value\": \"ID\"", "\"value\": \"ID \"", "spaces")]
    [InlineData("modifier extension", "\"use\": \"preauthorization\"", "\"use\": \"preauthorization\", \"modifierExtension\": [{ \"url\": \"http://example.org/x\", \"valueBoolean\": true }]", "modifierExtension")]
    public async Task A_request_this_service_cannot_read_faithfully_is_refused(string _, string find, string replace, string expected)
    {
        var bundle = PasFixtures.RequestBundle("ID").Replace(find, replace);

        await AssertRefused(await Post("/fhir/Claim/$submit", bundle), HttpStatusCode.BadRequest, expected);
    }

    [Fact]
    public async Task An_identifier_longer_than_200_characters_is_refused()
    {
        await AssertRefused(await Post("/fhir/Claim/$submit", PasFixtures.RequestBundle(new string('x', 201))), HttpStatusCode.BadRequest, "200 characters");
    }

    [Fact]
    public async Task A_Bundle_whose_first_entry_is_not_the_Claim_is_refused()
    {
        var bundle = JsonDocument.Parse(PasFixtures.RequestBundle(NewId())).RootElement;
        var entries = bundle.GetProperty("entry").EnumerateArray().Reverse().Select(e => e.GetRawText());
        var reordered = $$"""{ "resourceType": "Bundle", "type": "collection", "entry": [{{string.Join(",", entries)}}] }""";

        await AssertRefused(await Post("/fhir/Claim/$submit", reordered), HttpStatusCode.BadRequest, "first entry");
    }

    [Fact]
    public async Task Another_resource_type_is_refused_with_400_not_a_server_error()
    {
        var response = await Post("/fhir/Claim/$submit", """{ "resourceType": "Patient", "name": [{ "family": "X" }] }""");

        await AssertRefused(response, HttpStatusCode.BadRequest, "not a Patient");
    }

    [Fact]
    public async Task The_Bundle_may_be_sent_wrapped_in_Parameters()
    {
        var wrapped = $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "resource", "resource": {{PasFixtures.RequestBundle(NewId())}} }] }""";

        var response = await Post("/fhir/Claim/$submit", wrapped);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- An answer always describes what was reviewed ----------

    [Fact]
    public async Task A_request_number_cannot_be_reused_for_another_member()
    {
        var id = NewId();
        await Submit(PasFixtures.RequestBundle(id, memberId: "SYN-0001"));

        var reuse = await Post("/fhir/Claim/$submit", PasFixtures.RequestBundle(id, memberId: "SYN-0002", family: "Someoneelse"));

        await AssertRefused(reuse, HttpStatusCode.UnprocessableEntity, "different member");
    }

    [Fact]
    public async Task A_request_number_cannot_be_reused_for_another_service()
    {
        var id = NewId();
        await Submit(PasFixtures.RequestBundle(id, "H2019"));

        var reuse = await Post("/fhir/Claim/$submit", PasFixtures.RequestBundle(id, "H0019"));

        await AssertRefused(reuse, HttpStatusCode.UnprocessableEntity, "H0019");
    }

    [Fact]
    public async Task Every_reference_in_the_answer_resolves_inside_it()
    {
        var bundle = await Submit(PasFixtures.RequestBundle(NewId()));

        var entries = bundle.GetProperty("entry").EnumerateArray().ToList();
        var claimResponse = entries[0].GetProperty("resource");
        var patientReference = claimResponse.GetProperty("patient").GetProperty("reference").GetString();
        Assert.Contains(entries, e => e.GetProperty("fullUrl").GetString() == patientReference && e.GetProperty("resource").GetProperty("resourceType").GetString() == "Patient");
        Assert.EndsWith("/fhir/ClaimResponse/" + claimResponse.GetProperty("id").GetString(), entries[0].GetProperty("fullUrl").GetString());
        Assert.Equal(claimResponse.GetProperty("id").GetString(), claimResponse.GetProperty("identifier")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Reading_the_same_answer_twice_gives_the_same_created_time_and_a_cheap_not_modified()
    {
        var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId()))).GetProperty("id").GetString();

        var first = await _provider.GetAsync($"/fhir/ClaimResponse/{id}");
        await Task.Delay(1100);
        var second = await _provider.GetAsync($"/fhir/ClaimResponse/{id}");
        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/fhir/ClaimResponse/{id}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var notModified = await _provider.SendAsync(conditional);

        Assert.Equal(Created(await first.Content.ReadFromJsonAsync<JsonElement>()), Created(await second.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        static string? Created(JsonElement r) => r.GetProperty("created").GetString();
    }

    [Fact]
    public async Task An_inquiry_without_a_Claim_identifier_is_refused()
    {
        var bundle = PasFixtures.RequestBundle("ID").Replace("\"identifier\": [{ \"system\": \"http://example.org/provider/claim-id\", \"value\": \"ID\" }],", "");

        await AssertRefused(await Post("/fhir/Claim/$inquire", bundle), HttpStatusCode.BadRequest, "identifier");
    }

    [Fact]
    public async Task An_inquiry_must_match_the_identifier_system_too()
    {
        var id = NewId();
        await Submit(PasFixtures.RequestBundle(id));

        var otherSystem = await Post("/fhir/Claim/$inquire", PasFixtures.RequestBundle(id).Replace("http://example.org/provider/claim-id", "http://example.org/other"));

        Assert.False((await otherSystem.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("entry", out _));
    }

    // ---------- Retries and information requests ----------

    [Fact]
    public async Task Simultaneous_retries_of_one_request_all_get_the_same_answer()
    {
        var bundle = PasFixtures.RequestBundle(NewId());

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Post("/fhir/Claim/$submit", bundle)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => ClaimResponse(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()));
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task A_plain_retry_does_not_count_as_answering_an_information_request()
    {
        var document = Document("Assessment", "text/plain", "Synthetic assessment.");
        var bundle = PasFixtures.RequestBundle(NewId(), extraEntries: document);
        var id = ClaimResponse(await Submit(bundle)).GetProperty("id").GetString()!;
        await _staff.PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan" });

        await Submit(bundle); // the same Bundle again, nothing new in it

        var entry = await QueueEntry(id);
        Assert.Equal("InformationRequested", entry.GetProperty("status").GetString());
        Assert.Single(entry.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task Approve_and_deny_at_the_same_moment_cannot_both_succeed()
    {
        for (var round = 0; round < 5; round++)
        {
            var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId()))).GetProperty("id").GetString()!;
            var approve = _staff.PostAsJsonAsync($"/internal/requests/{id}/approve", new { through = "2099-12-31" });
            var deny = _staff.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reasonCode = "not-medically-necessary", reason = "Not medically necessary" });
            var results = await Task.WhenAll(approve, deny);

            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
            var entry = await QueueEntry(id);
            var approved = entry.GetProperty("status").GetString() == "Approved";
            Assert.Equal(approved, entry.GetProperty("authorizationNumber").ValueKind == JsonValueKind.String);
        }
    }

    // ---------- Storage ----------

    [Fact]
    public async Task Document_contents_are_stored_once_not_also_inside_the_kept_Bundle()
    {
        var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId(), extraEntries: Document("Assessment", "text/plain", "Synthetic assessment.")))).GetProperty("id").GetString();

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PriorAuthDb>().AuthorizationRequests.SingleAsync(r => r.Id == id);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic assessment.")), stored.SubmittedBundleJson);
        Assert.Contains("DocumentReference", stored.SubmittedBundleJson);
    }

    [Fact]
    public async Task The_queue_shows_open_requests_by_default_and_decided_ones_on_request()
    {
        var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId()))).GetProperty("id").GetString()!;
        await _staff.PostAsJsonAsync($"/internal/requests/{id}/deny", new { reasonCode = "not-medically-necessary", reason = "Not medically necessary" });

        var open = await _staff.GetFromJsonAsync<JsonElement>("/internal/requests?take=200");
        var decided = await _staff.GetFromJsonAsync<JsonElement>("/internal/requests?status=decided&take=200");

        Assert.DoesNotContain(open.EnumerateArray(), r => r.GetProperty("id").GetString() == id);
        Assert.Contains(decided.EnumerateArray(), r => r.GetProperty("id").GetString() == id);
    }

    // ---------- Questionnaire answers ----------

    [Fact]
    public async Task Questionnaire_answers_show_the_plans_wording_and_flag_gaps()
    {
        var answers = """
            { "fullUrl": "urn:uuid:55555555-5555-5555-5555-555555555555",
              "resource": { "resourceType": "QuestionnaireResponse", "questionnaire": "http://localhost/fhir/Questionnaire/dtr-mhp-day-treatment|1", "status": "in-progress",
                "item": [
                  { "linkId": "schedule", "answer": [{ "valueCoding": { "code": "full-day", "display": "whatever the EHR says" } }] },
                  { "linkId": "days-per-week", "answer": [{ "valueQuantity": { "value": 3, "unit": "days" } }] }
                ] } }
            """;
        var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId(), extraEntries: answers))).GetProperty("id").GetString()!;

        var document = Assert.Single((await QueueEntry(id)).GetProperty("documents").EnumerateArray());
        var text = await _staff.GetStringAsync($"/internal/requests/{id}/documents/{document.GetProperty("id").GetString()}");

        Assert.Contains("Program schedule: Full day", text);
        Assert.Contains("Days per week requested: 3 days", text);
        Assert.Contains("not completed", text);
        Assert.Contains("Required questions not answered: Date of the most recent assessment", text);
    }

    // ---------- Access ----------

    [Fact]
    public async Task A_deactivated_client_loses_read_access_at_once()
    {
        var id = ClaimResponse(await Submit(PasFixtures.RequestBundle(NewId()))).GetProperty("id").GetString();

        await _staff.PostAsync($"/internal/clients/{_registration.ClientId}/deactivate", null);
        var read = await _provider.GetAsync($"/fhir/ClaimResponse/{id}");

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal("OperationOutcome", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resourceType").GetString());
    }

    // ---------- Helpers ----------

    private static string NewId() => $"REQ-{Guid.NewGuid():N}";

    private Task<HttpResponseMessage> Post(string path, string json) =>
        _provider.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/fhir+json"));

    private async Task<JsonElement> Submit(string bundle)
    {
        var response = await Post("/fhir/Claim/$submit", bundle);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static JsonElement ClaimResponse(JsonElement bundle) => bundle.GetProperty("entry")[0].GetProperty("resource");

    private async Task<JsonElement> QueueEntry(string id) =>
        (await _staff.GetFromJsonAsync<JsonElement>("/internal/requests?status=all&take=200")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    private static string Document(string title, string contentType, string text) => $$"""
        { "resource": { "resourceType": "DocumentReference", "status": "current",
            "content": [{ "attachment": { "contentType": "{{contentType}}", "title": "{{title}}", "data": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}}" } }] } }
        """;

    private static async Task AssertRefused(HttpResponseMessage response, HttpStatusCode status, string expectedText)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Contains("OperationOutcome", body);
        Assert.Contains(expectedText, body);
    }
}
