using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.ClinicDemo;

/// <summary>A service on the plan's published list, as the presenter picks it.</summary>
public sealed record DemoService(string Id, string Name, string Program, string System, string Code, string[] Modifiers, string? QuestionnaireId);

/// <summary>
/// The clinic's connection to the plan: everything a provider's system does, kept out of the page.
/// One demo story at a time; calls are serialised so a double click cannot interleave them.
/// </summary>
public sealed class PlanConnection(string planApi)
{
    public const string ClinicName = "Sunrise Clinic (synthetic)";
    public const string DefaultServiceId = "H2019";
    private const string CoverageInformation = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly HttpClient _http = new() { BaseAddress = new Uri(planApi), Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N");
    private string? _clientId;
    private string? _token;
    private DateTime _tokenExpiresUtc;
    private List<DemoService>? _services;
    private DemoService? _service;
    private string? _questionnaireUrl;
    private JsonElement? _questionnaire;
    private string _requestNumber = NewRequestNumber();

    public void Reset()
    {
        _requestNumber = NewRequestNumber();
        _questionnaireUrl = null;
        _questionnaire = null;
    }

    /// <summary>Runs one step; any failure becomes a sentence the audience can read.</summary>
    public async Task<IResult> Run(Func<Task<object>> step)
    {
        await _gate.WaitAsync();
        try
        {
            return Results.Ok(await step());
        }
        catch (HttpRequestException)
        {
            return Results.Ok(new { ok = false, headline = $"The plan's system is not reachable at {planApi}. Start it and try again." });
        }
        catch (TaskCanceledException)
        {
            return Results.Ok(new { ok = false, headline = "The plan's system did not answer in time. Try again." });
        }
        catch (PlanRefusedException refused)
        {
            return Results.Ok(new { ok = false, headline = refused.Message, technical = refused.Body });
        }
        finally
        {
            _gate.Release();
        }
    }

    // The plan's published list of services. The presenter picks one; nothing about it is decided here.
    public async Task<object> ServicesAsync()
    {
        var services = await LoadServices();
        _service ??= services.FirstOrDefault(s => s.Id == DefaultServiceId) ?? services.First();
        return new { ok = true, selected = _service.Id, services };
    }

    public async Task<object> SelectAsync(string id)
    {
        var services = await LoadServices();
        _service = services.FirstOrDefault(s => s.Id == id) ?? throw new PlanRefusedException($"The plan's list has no service {id}.", "");
        Reset();
        return new { ok = true, selected = _service.Id };
    }

    private async Task<List<DemoService>> LoadServices()
    {
        if (_services is not null) return _services;
        var list = await _http.GetFromJsonAsync<JsonElement>("/requirements");
        _services = list.EnumerateArray().Select(r =>
        {
            var code = r.GetProperty("code").GetString()!;
            var modifiers = r.GetProperty("modifiers").EnumerateArray().Select(m => m.GetString()!).ToArray();
            return new DemoService(
                Id: modifiers.Length == 0 ? code : $"{code}:{string.Join(':', modifiers)}",
                Name: r.GetProperty("name").GetString()!,
                Program: r.GetProperty("program").GetString()!,
                System: r.GetProperty("system").GetString()!,
                Code: code,
                Modifiers: modifiers,
                QuestionnaireId: r.TryGetProperty("questionnaire", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null);
        }).ToList();
        return _services;
    }

    private async Task<DemoService> Service()
    {
        if (_service is null) await ServicesAsync();
        return _service!;
    }

    // Step 1 (CRD): while the clinician signs the order, ask the plan whether it needs approval.
    // An order carries the service code only, never billing modifiers, as in a real EHR.
    public async Task<object> CheckAsync()
    {
        var service = await Service();
        var hookUrl = $"{planApi}/cds-services/prior-auth-order-sign";
        var body = OrderSignHook(service);
        var watch = Stopwatch.StartNew();
        var (response, text) = await WithRegistration(async () =>
        {
            var call = new HttpRequestMessage(HttpMethod.Post, hookUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedStatement(hookUrl, includeSubject: false));
            return await _http.SendAsync(call);
        });
        watch.Stop();
        EnsureOk(response, text, "The plan could not answer the order check.");

        var parts = JsonDocument.Parse(text).RootElement
            .GetProperty("systemActions")[0].GetProperty("resource").GetProperty("extension").EnumerateArray()
            .First(e => e.GetProperty("url").GetString() == CoverageInformation)
            .GetProperty("extension");
        var paNeeded = Part(parts, "pa-needed", "valueCode");
        _questionnaireUrl = Part(parts, "questionnaire", "valueCanonical");
        var reason = parts.EnumerateArray().FirstOrDefault(e => e.GetProperty("url").GetString() == "reason") is { ValueKind: JsonValueKind.Object } r
            ? r.GetProperty("valueCodeableConcept").GetProperty("text").GetString()
            : null;

        string? formTitle = null;
        if (_questionnaireUrl is not null)
            formTitle = (await Package(_questionnaireUrl)).GetProperty("title").GetString();

        var (headline, tone) = paNeeded switch
        {
            "auth-needed" => ("Approval needed", "attention"),
            "no-auth" => ("No approval needed", "yes"),
            _ => ("A person at the plan needs to check", "attention"),
        };
        return new
        {
            ok = true,
            headline,
            tone,
            detail = formTitle is not null ? $"The plan asks the clinic to fill in its form: “{formTitle}”." : reason,
            elapsedMs = watch.ElapsedMilliseconds,
            hasForm = (_questionnaireUrl ?? FormUrl(service)) is not null,
            technical = Technical("CDS Hooks order-sign (CRD)", hookUrl, body, text),
        };
    }

    // Step 2 (DTR): get the plan's form. Answers are suggested from the pretend record; the presenter can change them.
    public async Task<object> FormAsync()
    {
        var service = await Service();
        var url = _questionnaireUrl ?? FormUrl(service);
        if (url is null) return new { ok = false, headline = "The plan has no form for this service." };

        var questionnaire = await Package(url);
        _questionnaireUrl = url;
        _questionnaire = questionnaire;
        var id = questionnaire.GetProperty("id").GetString() ?? "";
        var questions = questionnaire.GetProperty("item").EnumerateArray().Select(item =>
        {
            var linkId = item.GetProperty("linkId").GetString()!;
            return new
            {
                linkId,
                text = item.GetProperty("text").GetString(),
                type = item.GetProperty("type").GetString(),
                required = item.TryGetProperty("required", out var req) && req.GetBoolean(),
                options = item.TryGetProperty("answerOption", out var options)
                    ? options.EnumerateArray().Select(o => new { code = o.GetProperty("valueCoding").GetProperty("code").GetString(), display = o.GetProperty("valueCoding").GetProperty("display").GetString() }).ToArray()
                    : [],
                answer = SuggestedAnswers.For(id, linkId),
            };
        }).ToList();
        return new
        {
            ok = true,
            headline = questionnaire.GetProperty("title").GetString(),
            detail = questionnaire.TryGetProperty("status", out var s) && s.GetString() == "draft"
                ? "This form is still a draft until the plan's clinical lead signs it off."
                : null,
            questions,
            documents = new[] { "Assessment (synthetic)" },
            technical = Technical("DTR $questionnaire-package", $"{planApi}/fhir/Questionnaire/$questionnaire-package", PackageRequest(url), JsonSerializer.Serialize(questionnaire, Pretty)),
        };
    }

    // Step 3 (PAS): send the request, with the billing modifiers and the answers as the presenter left them.
    public async Task<object> SendAsync(Dictionary<string, JsonElement>? answers)
    {
        var service = await Service();
        return await Pas("/fhir/Claim/$submit", "PAS Claim/$submit", RequestBundle(service, answers));
    }

    // Step 4 (PAS): ask what the plan decided. An inquiry carries the request's identity, not the answers again.
    public async Task<object> StatusAsync() => await Pas("/fhir/Claim/$inquire", "PAS Claim/$inquire", RequestBundle(await Service(), answers: null));

    private string? FormUrl(DemoService service) => service.QuestionnaireId is null ? null : $"{planApi}/fhir/Questionnaire/{service.QuestionnaireId}";

    private async Task<object> Pas(string path, string label, string body)
    {
        var (response, text) = await WithSignIn(() => _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/fhir+json")));
        EnsureOk(response, text, "The plan could not take the request.");

        var claimResponse = JsonDocument.Parse(text).RootElement.GetProperty("entry")[0].GetProperty("resource");
        var code = claimResponse.GetProperty("item")[0].GetProperty("adjudication")[0].GetProperty("extension")[0]
            .GetProperty("extension")[0].GetProperty("valueCodeableConcept").GetProperty("coding")[0].GetProperty("code").GetString();
        var disposition = claimResponse.GetProperty("disposition").GetString() ?? "";
        var status = code switch
        {
            "A1" => "approved",
            "A3" => "denied",
            "NA" => "not-required",
            _ when disposition.StartsWith("More information", StringComparison.Ordinal) => "info",
            _ => "pended",
        };
        var headline = status switch
        {
            "approved" => "Approved",
            "denied" => "Denied",
            "not-required" => "No approval needed",
            "info" => "The reviewer needs more information",
            _ => "Waiting for a reviewer",
        };
        var authorizationNumber = claimResponse.TryGetProperty("preAuthRef", out var number) ? number.GetString() : null;
        if (status == "approved" && claimResponse.TryGetProperty("preAuthPeriod", out var period) && DateTime.TryParse(period.GetProperty("end").GetString(), out var end))
            disposition = $"Alex can start the service. Approved through {end:d MMMM yyyy}.";
        return new
        {
            ok = true,
            headline,
            detail = disposition,
            status,
            requestNumber = _requestNumber,
            authorizationNumber,
            technical = Technical(label, $"{planApi}{path}", body, text),
        };
    }

    private async Task<JsonElement> Package(string canonical)
    {
        var (response, text) = await WithSignIn(() => _http.PostAsync("/fhir/Questionnaire/$questionnaire-package",
            new StringContent(PackageRequest(canonical), Encoding.UTF8, "application/fhir+json")));
        EnsureOk(response, text, "The plan could not send its form.");
        return JsonDocument.Parse(text).RootElement.GetProperty("parameter")[0].GetProperty("resource")
            .GetProperty("entry")[0].GetProperty("resource").Clone();
    }

    // ---- Registration and sign-in. A restarted API forgets the clinic, so a refusal registers again once.

    private async Task<(HttpResponseMessage, string)> WithRegistration(Func<Task<HttpResponseMessage>> call)
    {
        await EnsureRegistered();
        var response = await call();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _clientId = null;
            _token = null;
            await EnsureRegistered();
            response = await call();
        }
        return (response, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpResponseMessage, string)> WithSignIn(Func<Task<HttpResponseMessage>> call)
    {
        await EnsureSignedIn();
        var response = await call();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;
            await EnsureSignedIn(registerAgain: true);
            response = await call();
        }
        return (response, await response.Content.ReadAsStringAsync());
    }

    private async Task EnsureRegistered()
    {
        if (_clientId is not null) return;
        var publicPart = _key.ExportParameters(includePrivateParameters: false);
        var keys = new { keys = new[] { new { kty = "RSA", kid = _keyId, use = "sig", alg = "RS384", n = Base64UrlEncoder.Encode(publicPart.Modulus), e = Base64UrlEncoder.Encode(publicPart.Exponent) } } };
        // Registration is the plan's onboarding step. It only works from the API's own machine.
        var call = new HttpRequestMessage(HttpMethod.Post, "/internal/clients") { Content = JsonContent.Create(new { name = ClinicName, jwks = keys }) };
        call.Headers.Add("X-Requested-By", "prior-auth-reviewer");
        var response = await _http.SendAsync(call);
        var text = await response.Content.ReadAsStringAsync();
        EnsureOk(response, text, "The plan would not register the clinic. Run the demo on the same machine as the API.");
        _clientId = JsonDocument.Parse(text).RootElement.GetProperty("clientId").GetString();
    }

    private async Task EnsureSignedIn(bool registerAgain = false)
    {
        if (registerAgain) _clientId = null;
        await EnsureRegistered();
        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return;

        for (var attempt = 0; ; attempt++)
        {
            var smart = await _http.GetFromJsonAsync<JsonElement>("/fhir/.well-known/smart-configuration");
            var tokenUrl = smart.GetProperty("token_endpoint").GetString()!;
            _http.DefaultRequestHeaders.Authorization = null;
            var response = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "system/Claim.write system/ClaimResponse.read system/Questionnaire.read",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = SignedStatement(tokenUrl, includeSubject: true),
            }));
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode && attempt == 0)
            {
                _clientId = null;
                await EnsureRegistered();
                continue;
            }
            EnsureOk(response, text, "The clinic could not sign in to the plan.");
            var token = JsonDocument.Parse(text).RootElement;
            _token = token.GetProperty("access_token").GetString();
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32() - 30);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return;
        }
    }

    // A short-lived statement signed with the clinic's private key. Each one is used once.
    private string SignedStatement(string audience, bool includeSubject)
    {
        var claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N") };
        if (includeSubject) claims["sub"] = _clientId!;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _clientId,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(2),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = _keyId }, SecurityAlgorithms.RsaSha384),
        });
    }

    // ---- The synthetic story.

    private static string NewRequestNumber() => $"DEMO-{DateTime.Now:yyyyMMdd-HHmmss}";

    // Dates on requests are the clinic's local calendar date.
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private static string OrderSignHook(DemoService service) => JsonSerializer.Serialize(new
    {
        hook = "order-sign",
        hookInstance = Guid.NewGuid().ToString(),
        context = new
        {
            userId = "Practitioner/synthetic-1",
            patientId = "synthetic-1",
            draftOrders = new
            {
                resourceType = "Bundle",
                type = "collection",
                entry = new[]
                {
                    new
                    {
                        resource = new
                        {
                            resourceType = "ServiceRequest",
                            id = "order-1",
                            status = "draft",
                            intent = "order",
                            code = new { coding = new[] { new { system = service.System, code = service.Code, display = service.Name } } },
                            subject = new { reference = "Patient/synthetic-1" },
                            authoredOn = Today,
                            requester = new { reference = "Practitioner/synthetic-1" },
                            insurance = new[] { new { reference = "Coverage/synthetic-1" } },
                        },
                    },
                },
            },
        },
    }, Pretty);

    private static string PackageRequest(string canonical) =>
        $$"""{ "resourceType": "Parameters", "parameter": [{ "name": "questionnaire", "valueCanonical": "{{canonical}}" }] }""";

    private string RequestBundle(DemoService service, Dictionary<string, JsonElement>? answers)
    {
        var entries = new List<object>
        {
            new
            {
                fullUrl = "urn:uuid:11111111-1111-1111-1111-111111111111",
                resource = new
                {
                    resourceType = "Claim",
                    identifier = new[] { new { system = "http://example.org/sunrise-clinic/request-id", value = _requestNumber } },
                    status = "active",
                    type = new { coding = new[] { new { system = "http://terminology.hl7.org/CodeSystem/claim-type", code = "professional" } } },
                    use = "preauthorization",
                    patient = new { reference = "urn:uuid:22222222-2222-2222-2222-222222222222" },
                    created = Today,
                    insurer = new { display = "County Behavioral Health Plan (synthetic)" },
                    provider = new { display = ClinicName },
                    priority = new { coding = new[] { new { system = "http://terminology.hl7.org/CodeSystem/processpriority", code = "normal" } } },
                    insurance = new[] { new { sequence = 1, focal = true, coverage = new { display = "Synthetic coverage" } } },
                    item = new[] { ClaimItem(service) },
                },
            },
            new
            {
                fullUrl = "urn:uuid:22222222-2222-2222-2222-222222222222",
                resource = new
                {
                    resourceType = "Patient",
                    identifier = new[] { new { system = "http://example.org/member-id", value = "SYN-0014" } },
                    name = new[] { new { family = "Testpatient", given = new[] { "Alex" } } },
                },
            },
        };

        if (answers is not null)
        {
            var document = Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic assessment for demonstration only. Not a real member."));
            entries.Add(new
            {
                fullUrl = "urn:uuid:33333333-3333-3333-3333-333333333333",
                resource = new
                {
                    resourceType = "DocumentReference",
                    status = "current",
                    content = new[] { new { attachment = new { contentType = "text/plain", title = "Assessment (synthetic)", data = document } } },
                },
            });
            if (_questionnaire is { } questionnaire && _questionnaireUrl is not null && ResponseItems(questionnaire, answers) is { Count: > 0 } items)
                entries.Add(new
                {
                    fullUrl = "urn:uuid:44444444-4444-4444-4444-444444444444",
                    resource = new { resourceType = "QuestionnaireResponse", questionnaire = _questionnaireUrl, status = "completed", item = items },
                });
        }

        return JsonSerializer.Serialize(new { resourceType = "Bundle", type = "collection", entry = entries }, Pretty);
    }

    // FHIR does not allow an empty list, so a service without billing modifiers sends none.
    private static Dictionary<string, object> ClaimItem(DemoService service)
    {
        var item = new Dictionary<string, object>
        {
            ["sequence"] = 1,
            ["productOrService"] = new { coding = new[] { new { system = service.System, code = service.Code } } },
        };
        if (service.Modifiers.Length > 0)
            item["modifier"] = service.Modifiers.Select(m => new { coding = new[] { new { system = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", code = m } } }).ToArray();
        return item;
    }

    // Turns the page's answers into FHIR answers of the type each question asks for. Blank answers are left out.
    private static List<object> ResponseItems(JsonElement questionnaire, Dictionary<string, JsonElement> answers)
    {
        var items = new List<object>();
        foreach (var item in questionnaire.GetProperty("item").EnumerateArray())
        {
            var linkId = item.GetProperty("linkId").GetString()!;
            if (!answers.TryGetValue(linkId, out var given)) continue;
            object? answer = item.GetProperty("type").GetString() switch
            {
                "boolean" when given.ValueKind is JsonValueKind.True or JsonValueKind.False => new { valueBoolean = given.GetBoolean() },
                "date" when given.GetString() is { Length: > 0 } date => new { valueDate = date },
                "choice" when given.GetString() is { Length: > 0 } code => item.GetProperty("answerOption").EnumerateArray()
                    .Select(o => o.GetProperty("valueCoding")).Where(c => c.GetProperty("code").GetString() == code)
                    .Select(c => (object)new { valueCoding = c.Clone() }).FirstOrDefault(),
                "boolean" or "date" or "choice" => null,
                _ when given.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(given.GetString()) => new { valueString = given.GetString()!.Trim() },
                _ => null,
            };
            if (answer is not null) items.Add(new { linkId, answer = new[] { answer } });
        }
        return items;
    }

    // ---- Helpers.

    private static string? Part(JsonElement extensions, string url, string valueName) =>
        extensions.EnumerateArray().FirstOrDefault(e => e.GetProperty("url").GetString() == url) is { ValueKind: JsonValueKind.Object } found
            ? found.GetProperty(valueName).GetString()
            : null;

    private static void EnsureOk(HttpResponseMessage response, string text, string message)
    {
        if (response.IsSuccessStatusCode) return;
        string? diagnostics = null;
        try
        {
            diagnostics = JsonDocument.Parse(text).RootElement.GetProperty("issue")[0].GetProperty("diagnostics").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
        }
        throw new PlanRefusedException(diagnostics is null ? $"{message} ({(int)response.StatusCode})" : $"{message} {diagnostics}", text);
    }

    private static object Technical(string what, string url, string sent, string received) => new
    {
        what,
        url,
        sent = Format(sent),
        received = Format(received),
    };

    private static string Format(string json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Pretty);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

public sealed class PlanRefusedException(string message, string body) : Exception(message)
{
    public string Body { get; } = body;
}
