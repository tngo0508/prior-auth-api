// A sample of what a provider's system does to use the Prior Authorization API.
// It is a teaching aid and a reference for providers' developers. Synthetic data only.
//
//   dotnet run --project samples/PriorAuth.SampleClient -- http://localhost:5020
//
// Steps: make a key pair, register the public key, ask whether an order needs authorization (CRD),
// download the questionnaire (DTR), submit the request with answers and a document (PAS), check its status.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var baseUrl = (args.FirstOrDefault() ?? "http://localhost:5020").TrimEnd('/');
// Keeps the plan admin's sign-in cookie for step 2.
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };

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
//    step done by a plan admin on the portal's Provider systems page, not by the provider's software.
//    Here the sample signs in as the Development test admin, which exists only when the API runs in Development.
//    The internal endpoints also refuse changes that do not carry the X-Requested-By header.
var adminSignIn = await http.PostAsync("/staff/dev-sign-in", new FormUrlEncodedContent([new("account", @"DEV\ari.admin"), new("returnUrl", "/")]));
if (adminSignIn.StatusCode != System.Net.HttpStatusCode.Redirect)
    throw new InvalidOperationException("Could not sign in as the test admin. Run the API in the Development environment.");
var registrationCall = new HttpRequestMessage(HttpMethod.Post, "/internal/clients") { Content = JsonContent.Create(new { name = "Sample Clinic (synthetic)", jwks = publicKeySet }) };
registrationCall.Headers.Add("X-Requested-By", "prior-auth-reviewer");
var registration = await http.SendAsync(registrationCall);
registration.EnsureSuccessStatusCode();
var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;
Step(2, $"Registered. The agency issued client id {clientId}.");

// 3. CRD: while the clinician writes the order, ask the plan whether it needs authorization.
//    CDS Hooks calls carry a statement signed with the private key, made for the service's own address.
const string serviceCode = "H2019";
var hookUrl = $"{baseUrl}/cds-services/prior-auth-order-sign";
var hookCall = new HttpRequestMessage(HttpMethod.Post, hookUrl)
{
    Content = new StringContent($$"""
        {
          "hook": "order-sign",
          "hookInstance": "{{Guid.NewGuid()}}",
          "context": {
            "userId": "Practitioner/synthetic-1",
            "patientId": "synthetic-1",
            "draftOrders": { "resourceType": "Bundle", "type": "collection", "entry": [{ "resource": {
              "resourceType": "ServiceRequest", "id": "order-1", "status": "draft", "intent": "order",
              "code": { "coding": [{ "system": "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "code": "{{serviceCode}}" }] },
              "subject": { "reference": "Patient/synthetic-1" },
              "authoredOn": "{{Today():yyyy-MM-dd}}",
              "requester": { "reference": "Practitioner/synthetic-1" },
              "insurance": [{ "reference": "Coverage/synthetic-1" }] } }] }
          }
        }
        """, Encoding.UTF8, "application/json"),
};
hookCall.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedStatement(audience: hookUrl, includeSubject: false));
var hookResponse = await http.SendAsync(hookCall);
hookResponse.EnsureSuccessStatusCode();
var coverageInformation = (await hookResponse.Content.ReadFromJsonAsync<JsonElement>())
    .GetProperty("systemActions")[0].GetProperty("resource").GetProperty("extension").EnumerateArray()
    .First(e => e.GetProperty("url").GetString() == "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information")
    .GetProperty("extension");
var paNeeded = Part(coverageInformation, "pa-needed", "valueCode");
var questionnaireUrl = Part(coverageInformation, "questionnaire", "valueCanonical");
Step(3, $"Asked about {serviceCode}. The plan says: prior authorization \"{paNeeded}\", questionnaire {questionnaireUrl}.");

// 4. Sign in for the rest. The token endpoint is published in the SMART configuration.
var smart = await http.GetFromJsonAsync<JsonElement>("/fhir/.well-known/smart-configuration");
var tokenUrl = smart.GetProperty("token_endpoint").GetString()!;
var tokenResponse = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["scope"] = "system/Claim.write system/ClaimResponse.read system/Questionnaire.read",
    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
    ["client_assertion"] = SignedStatement(audience: tokenUrl, includeSubject: true),
}));
tokenResponse.EnsureSuccessStatusCode();
var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
Step(4, $"Signed in. Access token is valid for {token.GetProperty("expires_in").GetInt32()} seconds.");

// 5. DTR: download the questionnaire the plan named.
var packageResponse = await http.PostAsync("/fhir/Questionnaire/$questionnaire-package", new StringContent(
    $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "questionnaire", "valueCanonical": "{{questionnaireUrl}}" }] }""",
    Encoding.UTF8, "application/fhir+json"));
packageResponse.EnsureSuccessStatusCode();
var questionnaire = (await packageResponse.Content.ReadFromJsonAsync<JsonElement>())
    .GetProperty("parameter")[0].GetProperty("resource").GetProperty("entry")[0].GetProperty("resource");
Step(5, $"Downloaded \"{questionnaire.GetProperty("title").GetString()}\". Questions:");
foreach (var item in questionnaire.GetProperty("item").EnumerateArray())
    Console.WriteLine($"     - {item.GetProperty("text").GetString()} ({item.GetProperty("type").GetString()}{(item.GetProperty("required").GetBoolean() ? ", required" : "")})");

// 6. PAS: submit the request with the answers and one document. In real life a clinician fills in the answers.
var requestId = $"SAMPLE-{DateTime.Now:yyyyMMdd-HHmmss}";
var submitted = await PostFhir("/fhir/Claim/$submit", RequestBundle(requestId, serviceCode, questionnaireUrl!));
Step(6, $"Submitted {requestId} with answers and a document. {Describe(submitted)}");

// 7. A person decides. Then ask again.
Console.WriteLine();
Console.WriteLine($"   Open {baseUrl}/ and decide the request, then press Enter here.");
Console.ReadLine();
Step(7, $"Status of {requestId}. {Describe(await PostFhir("/fhir/Claim/$inquire", RequestBundle(requestId, serviceCode, questionnaireUrl!)))}");

return;

static void Step(int number, string text) => Console.WriteLine($"{number}. {text}");

// Dates on requests are the clinic's local calendar date, not the UTC date.
static DateTime Today() => DateTime.Now.Date;

static string? Part(JsonElement extensions, string url, string valueName) =>
    extensions.EnumerateArray().FirstOrDefault(e => e.GetProperty("url").GetString() == url) is { ValueKind: JsonValueKind.Object } found
        ? found.GetProperty(valueName).GetString()
        : null;

// A short-lived statement signed with the private key. Each one is used once.
string SignedStatement(string audience, bool includeSubject)
{
    var claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N") };
    if (includeSubject) claims["sub"] = clientId;
    return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = clientId,
        Audience = audience,
        Claims = claims,
        Expires = DateTime.UtcNow.AddMinutes(2),
        SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.RsaSha384),
    });
}

async Task<JsonElement> PostFhir(string path, string json)
{
    var response = await http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/fhir+json"));
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{path} answered {(int)response.StatusCode}: {text}");
    var body = JsonDocument.Parse(text).RootElement;
    return body.GetProperty("entry")[0].GetProperty("resource");
}

static string Describe(JsonElement claimResponse)
{
    var code = claimResponse.GetProperty("item")[0].GetProperty("adjudication")[0].GetProperty("extension")[0]
        .GetProperty("extension")[0].GetProperty("valueCodeableConcept").GetProperty("coding")[0].GetProperty("code").GetString();
    var meaning = code switch { "A1" => "APPROVED", "A3" => "DENIED", "A4" => "PENDED", "NA" => "NO APPROVAL NEEDED", _ => code };
    return $"Answer: {meaning}. \"{claimResponse.GetProperty("disposition").GetString()}\"";
}

static string RequestBundle(string requestId, string code, string questionnaireUrl) => $$"""
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
            "created": "{{Today():yyyy-MM-dd}}",
            "insurer": { "display": "County Behavioral Health Plan (synthetic)" },
            "provider": { "display": "Sample Clinic (synthetic)" },
            "priority": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/processpriority", "code": "normal" }] },
            "insurance": [{ "sequence": 1, "focal": true, "coverage": { "display": "Synthetic coverage" } }],
            "item": [{ "sequence": 1, "productOrService": { "coding": [{ "system": "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "code": "{{code}}" }] } }]
          }
        },
        {
          "fullUrl": "urn:uuid:22222222-2222-2222-2222-222222222222",
          "resource": { "resourceType": "Patient", "identifier": [{ "system": "http://example.org/member-id", "value": "SYN-0001" }], "name": [{ "family": "Testpatient", "given": ["Alex"] }] }
        },
        {
          "fullUrl": "urn:uuid:33333333-3333-3333-3333-333333333333",
          "resource": {
            "resourceType": "DocumentReference",
            "status": "current",
            "content": [{ "attachment": { "contentType": "text/plain", "title": "Current assessment (synthetic)", "data": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic assessment text for demonstration only."))}}" } }]
          }
        },
        {
          "fullUrl": "urn:uuid:44444444-4444-4444-4444-444444444444",
          "resource": {
            "resourceType": "QuestionnaireResponse",
            "questionnaire": "{{questionnaireUrl}}",
            "status": "completed",
            "item": [
              { "linkId": "under-21", "answer": [{ "valueBoolean": true }] },
              { "linkId": "assessment-date", "answer": [{ "valueDate": "2026-10-30" }] },
              { "linkId": "diagnosis", "answer": [{ "valueString": "Synthetic example diagnosis." }] },
              { "linkId": "target-behaviours", "answer": [{ "valueString": "Synthetic example: elopement and aggression at school." }] },
              { "linkId": "placement-risk", "answer": [{ "valueBoolean": true }] },
              { "linkId": "amount-requested", "answer": [{ "valueString": "10 hours per week" }] }
            ]
          }
        }
      ]
    }
    """;
