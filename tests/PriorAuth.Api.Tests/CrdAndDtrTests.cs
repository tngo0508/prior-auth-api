using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PriorAuth.Api.Tests;

public class CrdAndDtrTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OrderSign = "/cds-services/prior-auth-order-sign";
    private const string CoverageInformation = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    private TestProvider _provider = null!;
    private HttpClient _signedIn = null!;
    private readonly HttpClient _anonymous = factory.CreateClient();

    public async Task InitializeAsync()
    {
        _provider = await TestProvider.RegisterAsync(factory.CreateClient());
        _signedIn = await _provider.SignInAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- CRD: is authorization needed? ----------

    [Fact]
    public async Task Discovery_lists_the_order_sign_and_order_select_services_without_a_token()
    {
        var discovery = await _anonymous.GetFromJsonAsync<JsonElement>("/cds-services");

        var hooks = discovery.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("hook").GetString()).ToArray();
        Assert.Equal(["order-sign", "order-select"], hooks);
    }

    [Fact]
    public async Task An_order_for_a_listed_service_comes_back_marked_as_needing_authorization_with_its_questionnaire()
    {
        var response = await CallOrderSign(Hook("order-sign", Order("H2019", coverage: "Coverage/cov-1")));
        var action = Assert.Single(response.GetProperty("systemActions").EnumerateArray());
        var information = CoverageInfo(action);

        Assert.Equal("update", action.GetProperty("type").GetString());
        Assert.Equal("ServiceRequest", action.GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("covered", Value(information, "covered", "valueCode"));
        Assert.Equal("auth-needed", Value(information, "pa-needed", "valueCode"));
        Assert.Equal("clinical", Value(information, "doc-needed", "valueCode"));
        Assert.EndsWith("/fhir/Questionnaire/prior-auth-h2019", Value(information, "questionnaire", "valueCanonical"));
        Assert.Equal("Coverage/cov-1", Sub(information, "coverage").GetProperty("valueReference").GetProperty("reference").GetString());
        Assert.False(string.IsNullOrEmpty(Value(information, "date", "valueDate")));
        Assert.False(string.IsNullOrEmpty(Value(information, "coverage-assertion-id", "valueString")));
    }

    [Fact]
    public async Task An_order_for_an_unlisted_service_comes_back_as_needing_no_authorization_and_without_claiming_coverage()
    {
        var response = await CallOrderSign(Hook("order-sign", Order("90834", coverage: "Coverage/cov-1")));
        var information = CoverageInfo(Assert.Single(response.GetProperty("systemActions").EnumerateArray()));

        Assert.Equal("no-auth", Value(information, "pa-needed", "valueCode"));
        Assert.Equal("conditional", Value(information, "covered", "valueCode"));
        Assert.DoesNotContain(information.GetProperty("extension").EnumerateArray(), e => e.GetProperty("url").GetString() == "questionnaire");
    }

    [Fact]
    public async Task Coverage_sent_ahead_by_the_EHR_is_used_when_the_order_names_none()
    {
        var prefetch = """{ "coverage": { "resourceType": "Bundle", "type": "searchset", "entry": [{ "resource": { "resourceType": "Coverage", "id": "prefetched-1" } }] } }""";

        var response = await CallOrderSign(Hook("order-sign", Order("H2019", coverage: null), prefetch));
        var information = CoverageInfo(Assert.Single(response.GetProperty("systemActions").EnumerateArray()));

        Assert.Equal("Coverage/prefetched-1", Sub(information, "coverage").GetProperty("valueReference").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task An_order_with_no_coverage_at_all_gets_an_explanation_instead_of_a_change()
    {
        var response = await CallOrderSign(Hook("order-sign", Order("H2019", coverage: null)));

        Assert.Empty(response.GetProperty("systemActions").EnumerateArray());
        Assert.Equal("warning", Assert.Single(response.GetProperty("cards").EnumerateArray()).GetProperty("indicator").GetString());
    }

    [Fact]
    public async Task A_hook_call_without_a_signed_token_is_refused()
    {
        var response = await _anonymous.PostAsync(OrderSign, Json(Hook("order-sign", Order("H2019", "Coverage/cov-1"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_token_made_for_a_different_service_is_refused()
    {
        var response = await Post(OrderSign, Hook("order-sign", Order("H2019", "Coverage/cov-1")), audience: "http://localhost/cds-services/prior-auth-order-select");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_call_for_the_wrong_hook_is_refused()
    {
        var response = await Post(OrderSign, Hook("patient-view", Order("H2019", "Coverage/cov-1")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_call_that_is_not_JSON_is_refused()
    {
        var response = await Post(OrderSign, "this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- DTR: what do you need to know? ----------

    [Fact]
    public async Task The_questionnaire_package_for_a_named_questionnaire_starts_with_the_questionnaire_and_includes_a_draft_response()
    {
        var parameters = $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "questionnaire", "valueCanonical": "http://localhost/fhir/Questionnaire/prior-auth-h2019|1" }] }""";

        var response = await _signedIn.PostAsync("/fhir/Questionnaire/$questionnaire-package", Fhir(parameters));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var package = Assert.Single(body.GetProperty("parameter").EnumerateArray());
        var entries = package.GetProperty("resource").GetProperty("entry");

        Assert.Equal("PackageBundle", package.GetProperty("name").GetString());
        Assert.Equal("collection", package.GetProperty("resource").GetProperty("type").GetString());
        Assert.Equal("Questionnaire", entries[0].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("QuestionnaireResponse", entries[1].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("in-progress", entries[1].GetProperty("resource").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_questionnaire_package_can_be_chosen_from_the_order_and_carries_the_services_questions()
    {
        var parameters = $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "order", "resource": {{Order("H0019", "Coverage/cov-1")}} }] }""";

        var response = await _signedIn.PostAsync("/fhir/Questionnaire/$questionnaire-package", Fhir(parameters));
        var questionnaire = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("parameter")[0].GetProperty("resource").GetProperty("entry")[0].GetProperty("resource");
        var asam = questionnaire.GetProperty("item").EnumerateArray().Single(i => i.GetProperty("linkId").GetString() == "asam-level");

        Assert.Equal("prior-auth-h0019", questionnaire.GetProperty("id").GetString());
        Assert.Equal("choice", asam.GetProperty("type").GetString());
        Assert.True(asam.GetProperty("required").GetBoolean());
        Assert.Equal(["3.1", "3.3", "3.5"], asam.GetProperty("answerOption").EnumerateArray().Select(o => o.GetProperty("valueCoding").GetProperty("display").GetString()!).ToArray());
    }

    [Fact]
    public async Task Asking_for_a_package_when_nothing_applies_returns_not_found()
    {
        var parameters = $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "order", "resource": {{Order("90834", "Coverage/cov-1")}} }] }""";

        var response = await _signedIn.PostAsync("/fhir/Questionnaire/$questionnaire-package", Fhir(parameters));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_questionnaire_can_be_read_by_its_id_and_declares_the_DTR_profile()
    {
        var questionnaire = await _signedIn.GetFromJsonAsync<JsonElement>("/fhir/Questionnaire/prior-auth-h2019");

        Assert.Equal("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire", questionnaire.GetProperty("meta").GetProperty("profile")[0].GetString());
        Assert.Equal("active", questionnaire.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(questionnaire.GetProperty("text").GetProperty("div").GetString()));
    }

    [Theory]
    [InlineData("GET", "/fhir/Questionnaire/prior-auth-h2019")]
    [InlineData("POST", "/fhir/Questionnaire/$questionnaire-package")]
    public async Task Questionnaires_need_a_token(string method, string path)
    {
        var response = await _anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_questionnaire_scope_cannot_read_questionnaires()
    {
        var submitOnly = await _provider.SignInAsync(factory, scope: "system/Claim.write");

        var response = await submitOnly.GetAsync("/fhir/Questionnaire/prior-auth-h2019");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- The answers reach the reviewer ----------

    [Fact]
    public async Task Questionnaire_answers_sent_with_a_request_reach_the_reviewer_as_readable_text()
    {
        var answers = """
            { "resource": { "resourceType": "QuestionnaireResponse", "questionnaire": "http://localhost/fhir/Questionnaire/prior-auth-h2019|1", "status": "completed",
                "item": [
                  { "linkId": "assessment-date", "answer": [{ "valueDate": "2026-10-30" }] },
                  { "linkId": "placement-risk", "answer": [{ "valueBoolean": true }] },
                  { "linkId": "hours-per-week", "answer": [{ "valueCoding": { "code": "11-to-20", "display": "11 to 20" } }] }
                ] } }
            """;
        var submitted = await _signedIn.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle($"REQ-{Guid.NewGuid():N}", answers)));
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString();

        var entry = (await _anonymous.GetFromJsonAsync<JsonElement>("/internal/requests")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var document = Assert.Single(entry.GetProperty("documents").EnumerateArray());
        var text = await _anonymous.GetStringAsync($"/internal/requests/{id}/documents/{document.GetProperty("id").GetString()}");

        Assert.Equal("Questionnaire answers: Therapeutic Behavioral Services", document.GetProperty("title").GetString());
        Assert.Contains("Date of the most recent assessment: 2026-10-30", text);
        Assert.Contains("Is the member at risk of a higher level of placement without TBS?: Yes", text);
        Assert.Contains("Requested hours per week: 11 to 20", text);
    }

    // ---------- Helpers ----------

    private static JsonElement CoverageInfo(JsonElement action) =>
        action.GetProperty("resource").GetProperty("extension").EnumerateArray().Single(e => e.GetProperty("url").GetString() == CoverageInformation);

    private static JsonElement Sub(JsonElement extension, string url) =>
        extension.GetProperty("extension").EnumerateArray().Single(e => e.GetProperty("url").GetString() == url);

    private static string? Value(JsonElement extension, string url, string valueName) => Sub(extension, url).GetProperty(valueName).GetString();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Fhir(string json) => new(json, Encoding.UTF8, "application/fhir+json");

    /// <summary>Calls a CDS service the way CDS Hooks requires: with a JWT the caller signed for that service's address.</summary>
    private Task<HttpResponseMessage> Post(string path, string body, string? audience = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.Assertion(audience: audience ?? "http://localhost" + path));
        return _anonymous.SendAsync(request);
    }

    private async Task<JsonElement> CallOrderSign(string body)
    {
        var response = await Post(OrderSign, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Order(string code, string? coverage) => $$"""
        { "resourceType": "ServiceRequest", "id": "order-1", "status": "draft", "intent": "order",
          "code": { "coding": [{ "system": "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "code": "{{code}}" }] },
          "subject": { "reference": "Patient/synthetic-1" }{{(coverage is null ? "" : $$""", "insurance": [{ "reference": "{{coverage}}" }]""")}} }
        """;

    private static string Hook(string hook, string order, string? prefetch = null) => $$"""
        {
          "hook": "{{hook}}",
          "hookInstance": "{{Guid.NewGuid()}}",
          "context": {
            "userId": "Practitioner/synthetic-1",
            "patientId": "synthetic-1",
            "draftOrders": { "resourceType": "Bundle", "type": "collection", "entry": [{ "resource": {{order}} }] }
          }{{(prefetch is null ? "" : $", \"prefetch\": {prefetch}")}}
        }
        """;

    private static string RequestBundle(string requestId, string extraEntry) =>
        PasFixtures.RequestBundle(requestId, extraEntries: extraEntry);
}
