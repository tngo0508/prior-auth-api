using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.")]
    [InlineData("")]
    public async Task A_malformed_assertion_is_refused_as_invalid_client(string assertion)
    {
        var response = await TestProvider.RequestToken(_anonymous, assertion);

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_ES384_client_can_sign_in()
    {
        var client = await SigningClient.EcP384().RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, client.Assertion());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_assertion_without_a_kid_is_refused()
    {
        var client = await SigningClient.Rsa().RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, client.Assertion(kid: ""));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_whose_kid_matches_no_registered_key_is_refused()
    {
        var client = await SigningClient.Rsa().RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, client.Assertion(kid: "some-other-key"));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task Only_the_key_named_by_kid_is_tried()
    {
        // Two keys registered; the token is signed with the first but names the second.
        var first = SigningClient.Rsa();
        var second = SigningClient.Rsa();
        var client = await first.RegisterAsync(_anonymous, new { keys = new[] { first.PublicJwk(), second.PublicJwk() } });

        var named = await TestProvider.RequestToken(_anonymous, client.Assertion());
        var misnamed = await TestProvider.RequestToken(_anonymous, client.Assertion(kid: second.KeyId));

        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        await AssertRefused(misnamed, "invalid_client");
    }

    [Fact]
    public async Task An_assertion_with_a_typ_other_than_JWT_is_refused_but_typ_may_be_left_out()
    {
        var client = await SigningClient.Rsa().RegisterAsync(_anonymous);

        var wrongTyp = await TestProvider.RequestToken(_anonymous, client.Assertion(typ: "at+jwt"));
        var noTyp = await TestProvider.RequestToken(_anonymous, client.Assertion(typ: ""));

        await AssertRefused(wrongTyp, "invalid_client");
        Assert.Equal(HttpStatusCode.OK, noTyp.StatusCode);
    }

    [Fact]
    public async Task An_assertion_issued_in_the_future_is_refused()
    {
        var client = await SigningClient.Rsa().RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, client.Assertion(issuedAt: DateTime.UtcNow.AddMinutes(3)));

        await AssertRefused(response, "invalid_client");
    }

    [Fact]
    public async Task Refusals_do_not_reveal_whether_a_client_id_exists()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        using var attackersKey = RSA.Create(2048);

        var unknown = await TestProvider.RequestToken(_anonymous, provider.Assertion(clientId: Guid.NewGuid().ToString("N")));
        var known = await TestProvider.RequestToken(_anonymous, provider.Assertion(signWith: attackersKey));

        Assert.Equal(await Description(unknown), await Description(known));
    }

    [Fact]
    public async Task Concurrent_requests_with_one_assertion_get_exactly_one_token()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        var assertion = provider.Assertion();
        var clients = Enumerable.Range(0, 20).Select(_ => factory.CreateClient()).ToList();

        var responses = await Task.WhenAll(clients.Select(http => TestProvider.RequestToken(http, assertion)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task Token_responses_are_never_cached()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var success = await TestProvider.RequestToken(_anonymous, provider.Assertion());
        var refused = await TestProvider.RequestToken(_anonymous, "a.b.c");
        var badRequest = await _anonymous.PostAsync("/auth/token", new StringContent("{}", Encoding.UTF8, "application/json"));

        foreach (var response in new[] { success, refused, badRequest })
        {
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Contains("no-cache", response.Headers.Pragma.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task With_a_configured_public_address_a_foreign_Host_header_cannot_change_the_audience()
    {
        // Host filtering (AllowedHosts) would already refuse the foreign host name with 400. Switch it off here to prove
        // the second defence on its own: the audience comes from configuration, never from the Host header.
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Auth:PublicBaseUrl", "http://localhost").UseSetting("AllowedHosts", "*"));
        var http = configured.CreateClient();
        var provider = await TestProvider.RegisterAsync(http);

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/auth/token") { Content = TokenForm(provider.Assertion(audience: "http://other-payer.example/auth/token")) };
        foreign.Headers.Host = "other-payer.example";
        var proper = new HttpRequestMessage(HttpMethod.Post, "/auth/token") { Content = TokenForm(provider.Assertion()) };
        proper.Headers.Host = "other-payer.example";

        await AssertRefused(await http.SendAsync(foreign), "invalid_client");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(proper)).StatusCode);
        var discovery = await http.GetFromJsonAsync<JsonElement>("/fhir/.well-known/smart-configuration");
        Assert.Equal("http://localhost/auth/token", discovery.GetProperty("token_endpoint").GetString());
    }

    // ---------- Scopes ----------

    [Theory]
    [InlineData("system/Claim.cud system/ClaimResponse.rs system/questionnaire.rs")]
    [InlineData("system/Claim.c system/ClaimResponse.r system/Questionnaire.rs")]
    [InlineData("system/Claim.write system/ClaimResponse.read system/Questionnaire.read")]
    public async Task SMART_v1_and_v2_scope_spellings_are_granted_as_asked_and_work(string scope)
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(), scope + " system/Patient.rs");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());

        Assert.Equal(scope, body.GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/fhir/ClaimResponse/anything")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await http.PostAsync("/fhir/Claim/$inquire", Fhir(RequestBundle("V2-SCOPES")))).StatusCode);
    }

    [Theory]
    [InlineData("system/Claim.rs")] // reading claims does not allow submitting them
    [InlineData("system/Claim.dc")] // out of order
    [InlineData("system/Claim.cud?status=active")] // parameters cannot be honoured
    [InlineData("system/*.rs")]
    [InlineData("system/claim.write")]
    public async Task Scopes_that_do_not_map_to_a_permission_are_refused(string scope)
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await TestProvider.RequestToken(_anonymous, provider.Assertion(), scope);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_v2_read_scope_alone_does_not_allow_submitting()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);
        var readOnly = await provider.SignInAsync(factory, scope: "system/ClaimResponse.rs");

        var response = await readOnly.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle("READ-ONLY")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_lists_what_SMART_requires()
    {
        var discovery = await _anonymous.GetFromJsonAsync<JsonElement>("/fhir/.well-known/smart-configuration");
        string[] Strings(string name) => discovery.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(["S256"], Strings("code_challenge_methods_supported"));
        Assert.Equal(["client_credentials"], Strings("grant_types_supported"));
        Assert.Contains("permission-v1", Strings("capabilities"));
        Assert.Contains("permission-v2", Strings("capabilities"));
        Assert.Contains("client-confidential-asymmetric", Strings("capabilities"));
        Assert.Contains("system/Claim.write", Strings("scopes_supported"));
        Assert.Contains("system/Claim.cud", Strings("scopes_supported"));
        Assert.Contains("system/ClaimResponse.rs", Strings("scopes_supported"));
        Assert.Contains("system/questionnaire.rs", Strings("scopes_supported"));
        Assert.Equal(["RS384", "ES384"], Strings("token_endpoint_auth_signing_alg_values_supported"));

        var authorize = await _anonymous.GetAsync(new Uri(discovery.GetProperty("authorization_endpoint").GetString()!).PathAndQuery);
        Assert.Equal(HttpStatusCode.BadRequest, authorize.StatusCode);
        Assert.Equal("unsupported_response_type", (await authorize.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // ---------- Registering ----------

    [Fact]
    public async Task Registering_a_key_set_that_contains_a_private_key_is_refused()
    {
        var provider = await TestProvider.RegisterAsync(_anonymous);

        var response = await _anonymous.PostAsJsonAsync("/internal/clients", new { name = "Careless Clinic (synthetic)", jwks = provider.PublicKeySet(includePrivatePart: true) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Registering_an_RSA_key_under_2048_bits_is_refused()
    {
        var response = await SigningClient.Rsa(1024).TryRegisterAsync(_anonymous);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "2048");
    }

    [Fact]
    public async Task Registering_a_symmetric_key_is_refused()
    {
        var response = await SigningClient.Rsa().TryRegisterAsync(_anonymous,
            new { keys = new[] { new { kty = "oct", kid = "shared", k = "c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0" } } });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "oct");
    }

    [Fact]
    public async Task Registering_an_EC_key_on_another_curve_is_refused()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = p256.ExportParameters(false).Q;
        var jwk = new { kty = "EC", kid = "p256", crv = "P-256", x = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(q.X), y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(q.Y) };

        var response = await SigningClient.Rsa().TryRegisterAsync(_anonymous, new { keys = new[] { jwk } });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "P-384");
    }

    [Fact]
    public async Task Registering_keys_without_a_kid_or_with_a_repeated_kid_is_refused()
    {
        var client = SigningClient.Rsa();
        var noKid = client.PublicJwk();
        noKid.Remove("kid");

        var missing = await client.TryRegisterAsync(_anonymous, new { keys = new[] { noKid } });
        var repeated = await client.TryRegisterAsync(_anonymous, new { keys = new[] { client.PublicJwk(), client.PublicJwk() } });

        await AssertProblem(missing, HttpStatusCode.UnprocessableEntity, "kid");
        await AssertProblem(repeated, HttpStatusCode.UnprocessableEntity, "kid");
    }

    [Theory]
    [InlineData("""{"name":"Clinic (synthetic)","jwks":{"keys":"not-an-array"}}""")]
    [InlineData("""{"name":"Clinic (synthetic)","jwks":{"keys":["not-an-object"]}}""")]
    [InlineData("""{"name":"Clinic (synthetic)","jwks":{"keys":[{"kty":5,"kid":"x"}]}}""")]
    [InlineData("""{"name":"Clinic (synthetic)","jwks":"keys"}""")]
    [InlineData("""{"name":"Clinic (synthetic)"}""")]
    [InlineData("""{"name":42,"jwks":{"keys":[]}}""")]
    [InlineData("""["not","an","object"]""")]
    public async Task Registration_bodies_of_the_wrong_shape_get_422(string body)
    {
        var response = await _anonymous.PostAsync("/internal/clients", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_registration_body_that_is_not_JSON_gets_400()
    {
        var response = await _anonymous.PostAsync("/internal/clients", new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- CDS Hooks calls (same assertion check, no sub) ----------

    [Fact]
    public async Task A_CDS_Hooks_JWT_without_sub_is_accepted()
    {
        var client = await SigningClient.EcP384().RegisterAsync(_anonymous);

        var response = await CdsCall(client.Assertion(audience: "http://localhost" + OrderSign, includeSubject: false));

        // Getting past sign-in to the body checks proves the token was accepted.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_CDS_Hooks_JWT_without_a_kid_or_used_twice_is_refused()
    {
        var client = await SigningClient.Rsa().RegisterAsync(_anonymous);
        var once = client.Assertion(audience: "http://localhost" + OrderSign, includeSubject: false);

        var noKid = await CdsCall(client.Assertion(audience: "http://localhost" + OrderSign, includeSubject: false, kid: ""));
        await CdsCall(once);
        var replay = await CdsCall(once);

        Assert.Equal(HttpStatusCode.Unauthorized, noKid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    // ---------- Start-up configuration ----------

    [Theory]
    [InlineData("Production", null, false)]
    [InlineData("Production", "http://pa.example.gov", false)]
    [InlineData("Production", "/relative", false)]
    [InlineData("Staging", "https://pa.example.gov", true)]
    [InlineData("Development", null, true)]
    public void The_public_address_must_be_https_outside_Development(string environment, string? publicBaseUrl, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:PublicBaseUrl"] = publicBaseUrl }).Build();
        var host = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };

        var error = Record.Exception(() => PriorAuth.Api.Auth.PublicUrl.ValidateConfiguration(config, host));

        if (valid) Assert.Null(error); else Assert.IsType<InvalidOperationException>(error);
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

        // An inquiry is a search: another provider finds nothing, so it gets an empty result.
        Assert.Equal(HttpStatusCode.OK, inquiryByB.StatusCode);
        Assert.False((await inquiryByB.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("entry", out _));
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

    private const string OrderSign = "/cds-services/prior-auth-order-sign";

    private Task<HttpResponseMessage> CdsCall(string jwt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OrderSign)
        {
            Content = new StringContent("""{"hook":"order-sign","hookInstance":"x","context":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        return _anonymous.SendAsync(request);
    }

    private static FormUrlEncodedContent TokenForm(string assertion) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["scope"] = TestProvider.AllScopes,
        ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
        ["client_assertion"] = assertion,
    });

    private static async Task AssertRefused(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    private static async Task<string?> Description(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_description").GetString();

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string detailContains)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(detailContains, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    private static async Task<string?> ResponseId(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString();

    private static StringContent Fhir(string json) => new(json, Encoding.UTF8, "application/fhir+json");

    private static string RequestBundle(string requestId) =>
        PasFixtures.RequestBundle(requestId, provider: "A Name The Request Claims");
}
