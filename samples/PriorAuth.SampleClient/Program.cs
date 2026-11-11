// A sample of what a provider's system does to use the Prior Authorization API.
// It is a teaching aid and a reference for providers' developers. Synthetic data only.
//
//   dotnet run --project samples/PriorAuth.SampleClient -- http://localhost:5020
//
// Steps: make a key pair, register the public key, sign in, submit a request with one document, check its status.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var baseUrl = (args.FirstOrDefault() ?? "http://localhost:5020").TrimEnd('/');
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

// 1. The provider creates a key pair. The private key never leaves the provider.
using var key = RSA.Create(2048);
var keyId = Guid.NewGuid().ToString("N");
var publicPart = key.ExportParameters(includePrivateParameters: false);
var publicKeySet = new
{
    keys = new[]
    {
        new { kty = "RSA", kid = keyId, use = "sig", alg = "RS384", n = Base64UrlEncoder.Encode(publicPart.Modulus), e = Base64UrlEncoder.Encode(publicPart.Exponent) },
    },
};
Step(1, "Created a key pair. Only the public half is shared.");

// 2. Agency staff register the provider's public key. In real life this is a one-time onboarding
//    step done by the agency, not by the provider's software. It only works from the server's own machine.
var registration = await http.PostAsJsonAsync("/internal/clients", new { name = "Sample Clinic (synthetic)", jwks = publicKeySet });
registration.EnsureSuccessStatusCode();
var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;
Step(2, $"Registered. The agency issued client id {clientId}.");

// 3. Find the token endpoint, then sign in by sending a short-lived statement signed with the private key.
var smart = await http.GetFromJsonAsync<JsonElement>("/fhir/.well-known/smart-configuration");
var tokenUrl = smart.GetProperty("token_endpoint").GetString()!;
var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Issuer = clientId,
    Audience = tokenUrl,
    Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString("N") },
    Expires = DateTime.UtcNow.AddMinutes(2),
    SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.RsaSha384),
});
var tokenResponse = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["scope"] = "system/Claim.write system/ClaimResponse.read",
    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
    ["client_assertion"] = assertion,
}));
tokenResponse.EnsureSuccessStatusCode();
var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
Step(3, $"Signed in. Access token is valid for {token.GetProperty("expires_in").GetInt32()} seconds.");

// 4. Ask whether the service needs approval and what paperwork is required.
const string serviceCode = "H2019";
var requirements = await http.GetFromJsonAsync<JsonElement>($"/requirements?code={serviceCode}");
Step(4, requirements.GetArrayLength() == 0
    ? $"{serviceCode} does not need approval."
    : $"{serviceCode} needs approval. Paperwork: {string.Join("; ", requirements[0].GetProperty("documentation").EnumerateArray().Select(d => d.GetString()))}.");

// 5. Submit the request.
var requestId = $"SAMPLE-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
var submitted = await PostFhir("/fhir/Claim/$submit", RequestBundle(requestId, serviceCode));
Step(5, $"Submitted {requestId}. {Describe(submitted)}");

// 6. A person decides. Then ask again.
Console.WriteLine();
Console.WriteLine($"   Open {baseUrl}/ and decide the request, then press Enter here.");
Console.ReadLine();
Step(6, $"Status of {requestId}. {Describe(await PostFhir("/fhir/Claim/$inquire", RequestBundle(requestId, serviceCode)))}");

return;

static void Step(int number, string text) => Console.WriteLine($"{number}. {text}");

async Task<JsonElement> PostFhir(string path, string json)
{
    var response = await http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/fhir+json"));
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{path} answered {(int)response.StatusCode}: {body}");
    return body.GetProperty("entry")[0].GetProperty("resource");
}

static string Describe(JsonElement claimResponse)
{
    var code = claimResponse.GetProperty("item")[0].GetProperty("adjudication")[0].GetProperty("extension")[0]
        .GetProperty("extension")[0].GetProperty("valueCodeableConcept").GetProperty("coding")[0].GetProperty("code").GetString();
    var meaning = code switch { "A1" => "APPROVED", "A3" => "DENIED", "A4" => "PENDED", "NA" => "NO APPROVAL NEEDED", _ => code };
    return $"Answer: {meaning}. \"{claimResponse.GetProperty("disposition").GetString()}\"";
}

static string RequestBundle(string requestId, string code) => $$"""
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
            "use": "preauthorization",
            "patient": { "reference": "urn:uuid:22222222-2222-2222-2222-222222222222" },
            "created": "{{DateTime.UtcNow:yyyy-MM-dd}}",
            "insurer": { "display": "County Behavioral Health Plan (synthetic)" },
            "provider": { "display": "Sample Clinic (synthetic)" },
            "priority": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/processpriority", "code": "normal" }] },
            "insurance": [{ "sequence": 1, "focal": true, "coverage": { "display": "Synthetic coverage" } }],
            "item": [{ "sequence": 1, "productOrService": { "coding": [{ "code": "{{code}}" }] } }]
          }
        },
        {
          "fullUrl": "urn:uuid:22222222-2222-2222-2222-222222222222",
          "resource": { "resourceType": "Patient", "name": [{ "family": "Testpatient", "given": ["Alex"] }] }
        },
        {
          "fullUrl": "urn:uuid:33333333-3333-3333-3333-333333333333",
          "resource": {
            "resourceType": "DocumentReference",
            "status": "current",
            "content": [{ "attachment": { "contentType": "text/plain", "title": "Current assessment (synthetic)", "data": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic assessment text for demonstration only."))}}" } }]
          }
        }
      ]
    }
    """;
