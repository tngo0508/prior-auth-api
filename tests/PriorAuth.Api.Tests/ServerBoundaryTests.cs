using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PriorAuth.Api.Tests;

/// <summary>What the server does at its edges: who may reach the internal endpoints, what headers protect answers, and how the FHIR API refuses what it does not speak.</summary>
public class ServerBoundaryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // ---------- Internal endpoints ----------

    [Fact]
    public async Task The_review_queue_refuses_a_caller_on_another_machine()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/requests");
        request.Headers.Add(ApiFactory.RemoteAddressHeader, "203.0.113.7");

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_review_queue_refuses_a_request_forwarded_by_a_proxy()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/requests");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_request_addressed_to_another_host_name_is_refused()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/requests");
        request.Headers.Host = "attacker.example";

        Assert.NotEqual(HttpStatusCode.OK, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task An_internal_change_without_the_review_page_header_is_refused()
    {
        var bare = factory.CreateClient();
        bare.DefaultRequestHeaders.Remove(PriorAuth.Api.Auth.AuthSetup.InternalRequestHeader);

        var response = await bare.PostAsync("/internal/clients/anything/deactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Headers that protect answers ----------

    [Fact]
    public async Task Answers_are_never_cached_and_the_review_page_cannot_be_framed()
    {
        var client = factory.CreateClient();

        var queue = await client.GetAsync("/internal/requests");
        var page = await client.GetAsync("/");

        Assert.True(queue.Headers.CacheControl?.NoStore);
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(page.Headers.Contains("Server"));
    }

    // ---------- The FHIR API speaks FHIR JSON ----------

    [Fact]
    public async Task Asking_for_XML_is_answered_406()
    {
        var response = await factory.CreateClient().GetAsync("/fhir/metadata?_format=xml");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_is_answered_415()
    {
        var provider = await (await TestProvider.RegisterAsync(factory.CreateClient())).SignInAsync(factory);

        var response = await provider.PostAsync("/fhir/Claim/$submit", new StringContent("<Bundle/>", Encoding.UTF8, "application/fhir+xml"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Errors_without_a_body_still_come_back_as_OperationOutcomes()
    {
        var client = factory.CreateClient();

        var unknown = await client.GetAsync("/fhir/Nothing/here");
        var noToken = await client.GetAsync("/fhir/ClaimResponse/abc");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("OperationOutcome", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resourceType").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal("login", (await noToken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issue")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_capability_statement_describes_the_instance_and_the_questionnaire_endpoints()
    {
        var capability = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/fhir/metadata");

        Assert.True(capability.TryGetProperty("implementation", out _));
        Assert.True(capability.TryGetProperty("software", out _));
        var types = capability.GetProperty("rest")[0].GetProperty("resource").EnumerateArray().Select(r => r.GetProperty("type").GetString()).ToList();
        Assert.Contains("Questionnaire", types);
    }

    [Fact]
    public async Task The_health_endpoint_reports_healthy_when_the_database_answers()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
