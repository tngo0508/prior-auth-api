using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Review;

namespace PriorAuth.Api.Tests;

/// <summary>The staff portal's pages, signed in as the Development test users.</summary>
public class PortalTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private HttpClient _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await (await TestProvider.RegisterAsync(factory.CreateClient())).SignInAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_clinician_claims_and_approves_a_request_on_its_page()
    {
        var id = await NewPendedRequest();
        var clinician = factory.SignedInAs(ApiFactory.ClinicianAccount);

        var claimed = await Act(clinician, id, "Claim", []);
        var approved = await Act(clinician, id, "Approve", [new("through", "2099-12-31")]);

        Assert.Contains("You are working on this request.", claimed);
        Assert.Contains("Approved.", approved);
        var entry = await QueueEntry(id);
        Assert.Equal("Approved", entry.GetProperty("status").GetString());
        Assert.Equal(ApiFactory.ClinicianAccount, entry.GetProperty("decidedByAccount").GetString());
    }

    [Fact]
    public async Task The_request_page_lists_the_providers_documents_as_downloads()
    {
        var document = $$"""
            { "resource": { "resourceType": "DocumentReference", "status": "current",
                "content": [{ "attachment": { "contentType": "text/plain", "title": "Assessment", "data": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic assessment."))}}" } }] } }
            """;
        var id = await NewPendedRequest(document);

        var page = await factory.SignedInAs(ApiFactory.ClinicianAccount).GetStringAsync($"/requests/{id}");

        Assert.Matches($"<a href=\"/internal/requests/{id}/documents/[^\"]+\">Assessment</a>", page);
        Assert.Contains("text/plain", page);
    }

    [Fact]
    public async Task A_coordinator_sees_no_decision_forms_but_can_ask_for_information()
    {
        var id = await NewPendedRequest();

        var page = await factory.SignedInAs(ApiFactory.CoordinatorAccount).GetStringAsync($"/requests/{id}");

        Assert.DoesNotContain("handler=Approve", page);
        Assert.DoesNotContain("handler=Deny", page);
        Assert.Contains("handler=RequestInformation", page);
        Assert.Contains("Only a licensed clinician or a physician can approve or deny.", page);
    }

    [Fact]
    public async Task A_decision_from_a_page_that_is_out_of_date_is_refused()
    {
        var id = await NewPendedRequest();
        var clinician = factory.SignedInAs(ApiFactory.ClinicianAccount);
        var page = await clinician.GetStringAsync($"/requests/{id}");
        await factory.SignedInAs(ApiFactory.CoordinatorAccount).PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan" });

        var answer = await Post(clinician, id, "Approve", page, [new("through", "2099-12-31")]);

        Assert.Contains(WebUtility.HtmlEncode(ReviewDesk.ConflictMessage), answer);
        Assert.Equal("InformationRequested", (await QueueEntry(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_denial_on_the_page_needs_a_reason_from_the_plans_list()
    {
        var id = await NewPendedRequest();
        var clinician = factory.SignedInAs(ApiFactory.ClinicianAccount);

        var answer = await Act(clinician, id, "Deny", [new("reasonCode", "made-up"), new("reason", "No.")]);

        Assert.Contains("Choose a denial reason from the plan", answer);
        Assert.Equal("Pended", (await QueueEntry(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_admin_lands_on_the_provider_systems_page()
    {
        var response = await factory.SignedInAs(ApiFactory.AdminAccount).GetAsync("/");

        Assert.Equal("/providers", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Register a provider system", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_provider_system_can_be_registered_with_a_contact_email_and_a_bad_one_is_refused()
    {
        var admin = factory.SignedInAs(ApiFactory.AdminAccount);
        var keys = new TestProvider().PublicKeySet();

        var good = await admin.PostAsJsonAsync("/internal/clients", new { name = "Contact Clinic (synthetic)", contactEmail = "pa-desk@clinic.example", jwks = keys });
        var bad = await admin.PostAsJsonAsync("/internal/clients", new { name = "Contact Clinic (synthetic)", contactEmail = "not an address", jwks = keys });
        var clientId = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString();
        var listed = (await admin.GetFromJsonAsync<JsonElement>("/internal/clients")).EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == clientId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("pa-desk@clinic.example", listed.GetProperty("contactEmail").GetString());
    }

    [Fact]
    public async Task The_internal_endpoints_apply_the_same_role_rules()
    {
        var id = await NewPendedRequest();

        var byCoordinator = await factory.SignedInAs(ApiFactory.CoordinatorAccount)
            .PostAsJsonAsync($"/internal/requests/{id}/deny", new { reasonCode = "not-medically-necessary", reason = "No." });
        var claimed = await factory.SignedInAs(ApiFactory.ClinicianAccount).PostAsync($"/internal/requests/{id}/claim", null);
        var byPhysician = await factory.SignedInAs(ApiFactory.PhysicianAccount)
            .PostAsJsonAsync($"/internal/requests/{id}/approve", new { through = "2099-12-31" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, byCoordinator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byPhysician.StatusCode);
        Assert.Contains("Lee Clinician", await byPhysician.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Someone_without_the_role_for_a_page_is_told_who_they_are_signed_in_as()
    {
        var page = await factory.SignedInAs(ApiFactory.AdminAccount).GetAsync("/requests/anything");

        Assert.Equal("/no-access", page.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains(@"You are signed in as <strong>DEV\ari.admin</strong>", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Windows_group_named_like_a_portal_role_does_not_grant_it()
    {
        // Windows answers IsInRole for any group of that name. The portal must only trust its own role claim.
        var windowsUser = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.Name, @"COUNTY\someone"), new(System.Security.Claims.ClaimTypes.Role, "Admin")],
            "Negotiate", System.Security.Claims.ClaimTypes.Name, System.Security.Claims.ClaimTypes.Role));
        var authorization = factory.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(windowsUser, resource: null, StaffSignIn.AdminPolicy);

        Assert.True(windowsUser.IsInRole("Admin"));
        Assert.False(result.Succeeded);
        Assert.Empty(windowsUser.CurrentStaff()!.Roles);
    }

    // ---------- Sign-in set-up, without a web server ----------

    [Fact]
    public void Roles_come_only_from_the_configured_groups()
    {
        var config = Config(new()
        {
            ["StaffSignIn:Groups:LicensedClinician"] = @"COUNTY\PA-Clinicians",
            ["StaffSignIn:Groups:Admin"] = @"COUNTY\PA-Admins",
            ["StaffSignIn:Groups:Coordinator"] = "",
        });
        string[] memberOf = [@"COUNTY\PA-Clinicians", @"COUNTY\PA-Coordinators", ""];

        var roles = StaffSignIn.RolesFromGroups(memberOf.Contains, config);

        Assert.Equal([StaffRole.LicensedClinician], roles);
    }

    [Fact]
    public void The_test_user_sign_in_is_refused_outside_the_Development_environment()
    {
        var production = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };

        var refused = Assert.Throws<InvalidOperationException>(() => StaffSignIn.Mode(Config(new() { ["StaffSignIn:Mode"] = "Development" }), production));

        Assert.Contains("refused outside the Development environment", refused.Message);
        Assert.Equal(StaffSignInMode.Windows, StaffSignIn.Mode(Config(new()), production));
    }

    // ---------- Helpers ----------

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private async Task<string> NewPendedRequest(params string[] extraEntries)
    {
        var response = await _provider.PostAsync("/fhir/Claim/$submit",
            new StringContent(PasFixtures.RequestBundle($"REQ-{Guid.NewGuid():N}", extraEntries: extraEntries), Encoding.UTF8, "application/fhir+json"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString()!;
    }

    /// <summary>Opens the request page and submits one of its forms, as a browser would.</summary>
    private static async Task<string> Act(HttpClient staff, string id, string handler, KeyValuePair<string, string>[] fields) =>
        await Post(staff, id, handler, await staff.GetStringAsync($"/requests/{id}"), fields);

    private static async Task<string> Post(HttpClient staff, string id, string handler, string page, KeyValuePair<string, string>[] fields)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var version = Regex.Match(page, "name=\"Version\" value=\"(\\d+)\"").Groups[1].Value;
        var response = await staff.PostAsync($"/requests/{id}?handler={handler}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Version", version), .. fields]));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return text;
    }

    private async Task<JsonElement> QueueEntry(string id) =>
        (await factory.CreateClient().GetFromJsonAsync<JsonElement>("/internal/requests?status=all&take=200"))
        .EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
}
