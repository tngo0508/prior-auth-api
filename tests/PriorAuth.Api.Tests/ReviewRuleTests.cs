using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PriorAuth.Api.Tests;

/// <summary>
/// A submitted request gets the review the plan's list sets for its service: prior authorization, concurrent review,
/// or none. The billing modifiers on the request line decide between entries that share a code.
/// </summary>
public class ReviewRuleTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string RevenueCodes = "https://www.nubc.org/CodeSystem/RevenueCodes";

    private HttpClient _provider = null!;
    private HttpClient _staff = null!;

    public async Task InitializeAsync()
    {
        _staff = factory.CreateClient();
        _provider = await (await TestProvider.RegisterAsync(_staff)).SignInAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_residential_level_sent_with_its_modifier_is_pended_for_prior_authorization()
    {
        var (response, queued) = await Submit(Item("H0019", PasFixtures.Hcpcs, "U1"));

        Assert.Equal("queued", response.GetProperty("outcome").GetString());
        Assert.Equal("PriorAuthorization", queued.GetProperty("reviewType").GetString());
        Assert.Equal("ASAM level 3.1 clinically managed low-intensity residential treatment", queued.GetProperty("serviceName").GetString());
    }

    [Fact]
    public async Task The_same_code_without_a_level_modifier_is_pended_for_a_person_to_check_not_waved_through()
    {
        // H0019 alone is adult residential (no prior authorization), or a DMC-ODS residential request missing its level modifier.
        var (response, queued) = await Submit(Item("H0019", PasFixtures.Hcpcs));

        Assert.Equal("queued", response.GetProperty("outcome").GetString());
        Assert.Equal("PriorAuthorization", queued.GetProperty("reviewType").GetString());
        Assert.Contains("H0019:U1", History(queued));
    }

    [Fact]
    public async Task A_listed_service_the_plan_may_not_prior_authorize_is_answered_not_required()
    {
        var (response, queued) = await Submit(Item("H0018", PasFixtures.Hcpcs));

        Assert.Equal("complete", response.GetProperty("outcome").GetString());
        Assert.Equal("NotRequired", queued.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("reviewType").ValueKind);
        Assert.Contains("may not require prior authorization", History(queued));
    }

    [Fact]
    public async Task An_inpatient_admission_is_pended_for_concurrent_review()
    {
        var (response, queued) = await Submit(Item("0100", RevenueCodes));

        Assert.Equal("queued", response.GetProperty("outcome").GetString());
        Assert.Equal("ConcurrentReview", queued.GetProperty("reviewType").GetString());
        Assert.Contains("Pended for concurrent review", History(queued));
    }

    [Fact]
    public async Task A_service_not_on_the_list_needs_no_authorization()
    {
        var (response, queued) = await Submit(Item("90834", PasFixtures.Cpt));

        Assert.Equal("complete", response.GetProperty("outcome").GetString());
        Assert.Equal("NotRequired", queued.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_code_that_cannot_be_recognised_is_pended_for_a_person_to_check_not_waved_through()
    {
        var (response, queued) = await Submit(Item("TBS-1", "http://example.org/local-codes"));

        Assert.Equal("queued", response.GetProperty("outcome").GetString());
        Assert.Equal("Pended", queued.GetProperty("status").GetString());
        Assert.Contains("was not recognised", History(queued));
    }

    /// <summary>A Claim.item array holding one service, with its billing modifiers on the line.</summary>
    private static string Item(string code, string system, params string[] modifiers)
    {
        var modifierList = modifiers.Select(m => new { coding = new[] { new { system = PasFixtures.Hcpcs, code = m } } }).ToArray();
        object item = modifiers.Length == 0
            ? new { sequence = 1, productOrService = new { coding = new[] { new { system, code } } } }
            : new { sequence = 1, productOrService = new { coding = new[] { new { system, code } } }, modifier = modifierList };
        return JsonSerializer.Serialize(new[] { item });
    }

    /// <summary>Submits a request with the given Claim.item array; returns the ClaimResponse and the request as the reviewer queue shows it.</summary>
    private async Task<(JsonElement Response, JsonElement Queued)> Submit(string items)
    {
        var sent = await _provider.PostAsync("/fhir/Claim/$submit",
            new StringContent(PasFixtures.RequestBundle($"REQ-{Guid.NewGuid():N}", items: items), Encoding.UTF8, "application/fhir+json"));
        var text = await sent.Content.ReadAsStringAsync();
        Assert.True(sent.IsSuccessStatusCode, text);
        var response = JsonDocument.Parse(text).RootElement.GetProperty("entry")[0].GetProperty("resource");
        var id = response.GetProperty("id").GetString();
        var queued = (await _staff.GetFromJsonAsync<JsonElement>("/internal/requests?status=all&take=200"))
            .EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        return (response, queued);
    }

    private static string History(JsonElement queued) =>
        string.Join(" | ", queued.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("text").GetString()));
}
