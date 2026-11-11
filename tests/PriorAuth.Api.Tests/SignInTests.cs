using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PriorAuth.Api.Tests;

public class SignInTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _anonymous = factory.CreateClient();

    // ---------- Getting a token ----------

    [Fact]
    public async Task A_registered_client_with_a_valid_assertion_gets_a_short_lived_token()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(300, body.GetProperty("expires_in").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task An_assertion_signed_with_someone_elses_key_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        using var attackersKey = RSA.Create(2048);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(signWith: attackersKey));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_from_an_unregistered_client_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(clientId: "not-a-registered-client"));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_addressed_to_a_different_server_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(audience: "https://some-other-payer.example/auth/token"));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_expired_assertion_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(expiresIn: TimeSpan.FromMinutes(-10)));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_that_lives_longer_than_five_minutes_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(expiresIn: TimeSpan.FromHours(1)));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_cannot_be_used_twice()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        var assertion = provider.Assertion();

        var first = await TestProvider.RequestToken(_anonymous, assertion);
        var replay = await TestProvider.RequestToken(_anonymous, assertion);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertRefused(replay, "invalid_client");
    }

    [Fact]
    public async Task A_deactivated_client_cannot_get_a_token()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        await _anonymous.PostAsync($"/internal/clients/{provider.ClientId}/deactivate", null);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion());

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task A_request_for_no_supported_scope_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(), scope: "system/Patient.read");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // ---------- Registering ----------

    [Fact]
    public async Task Registering_a_key_set_that_contains_a_private_key_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await _anonymous.PostAsJsonAsync("/internal/clients", new { name = "Careless Clinic (synthetic)", jwks = provider.PublicKeySet(includePrivatePart: true) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- Using the API ----------

    [Theory]
    [InlineData("POST", "/fhir/Claim/$submit")]
    [InlineData("POST", "/fhir/Claim/$inquire")]
    [InlineData("GET", "/fhir/ClaimResponse/anything")]
    public async Task Without_a_token_the_request_endpoints_answer_401(string method, string path)
    {
        var response = await _anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/fhir/metadata")]
    [InlineData("/requirements")]
    [InlineData("/fhir/.well-known/smart-configuration")]
    public async Task The_descriptive_endpoints_need_no_token(string path)
    {
        var response = await _anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_read_scope_cannot_read_responses()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        var submitOnly = await provider.SignInAsync(factory, scope: "system/Claim.write");

        var response = await submitOnly.GetAsync("/fhir/ClaimResponse/anything");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_cannot_see_another_providers_request()
    {
        var clinicA = await (await TestProvider.RegisterAsync(_anonymous, "Clinic A (synthetic)")).SignInAsync(factory);
        var clinicB = await (await TestProvider.RegisterAsync(_anonymous, "Clinic B (synthetic)")).SignInAsync(factory);
        var submitted = await clinicA.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle("SHARED-ID")));
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString();

        var inquiryByB = await clinicB.PostAsync("/fhir/Claim/$inquire", Fhir(RequestBundle("SHARED-ID")));
        var readByB = await clinicB.GetAsync($"/fhir/ClaimResponse/{id}");
        var readByA = await clinicA.GetAsync($"/fhir/ClaimResponse/{id}");

        Assert.Equal(HttpStatusCode.NotFound, inquiryByB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, readByB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readByA.StatusCode);
    }

    [Fact]
    public async Task Two_providers_can_use_the_same_request_identifier_without_colliding()
    {
        var clinicA = await (await TestProvider.RegisterAsync(_anonymous, "Clinic A (synthetic)")).SignInAsync(factory);
        var clinicB = await (await TestProvider.RegisterAsync(_anonymous, "Clinic B (synthetic)")).SignInAsync(factory);

        var fromA = await clinicA.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle("SAME-NUMBER")));
        var fromB = await clinicB.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle("SAME-NUMBER")));

        Assert.NotEqual(await ResponseId(fromA), await ResponseId(fromB));
    }

    [Fact]
    public async Task The_request_is_recorded_under_the_registered_name_not_the_name_the_request_claims()
    {
        var clinic = await (await TestProvider.RegisterAsync(_anonymous, "Registered Clinic Name (synthetic)")).SignInAsync(factory);
        var id = await ResponseId(await clinic.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle($"REQ-{Guid.NewGuid():N}"))));

        var queue = await _anonymous.GetFromJsonAsync<JsonElement>("/internal/requests");
        var request = queue.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

        Assert.Equal("Registered Clinic Name (synthetic)", request.GetProperty("providerName").GetString());
    }

    // ---------- Helpers ----------

    private static async Task AssertRefused(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    private static async Task<string?> ResponseId(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString();

    private static StringContent Fhir(string json) => new(json, Encoding.UTF8, "application/fhir+json");

    private static string RequestBundle(string requestId) => $$"""
        {
          "resourceType": "Bundle",
          "type": "collection",
          "entry": [
            {
              "resource": {
                "resourceType": "Claim",
                "identifier": [{ "value": "{{requestId}}" }],
                "status": "active",
                "type": { "coding": [{ "code": "professional" }] },
                "use": "preauthorization",
                "patient": { "display": "Synthetic Patient" },
                "created": "2026-11-11",
                "insurer": { "display": "County Behavioral Health Plan (synthetic)" },
                "provider": { "display": "A Name The Request Claims" },
                "priority": { "coding": [{ "code": "normal" }] },
                "insurance": [{ "sequence": 1, "focal": true, "coverage": { "display": "Synthetic coverage" } }],
                "item": [{ "sequence": 1, "productOrService": { "coding": [{ "code": "H2019" }] } }]
              }
            }
          ]
        }
        """;
}
