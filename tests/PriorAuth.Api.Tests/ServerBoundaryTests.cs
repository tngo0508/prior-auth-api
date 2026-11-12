using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;

namespace PriorAuth.Api.Tests;

/// <summary>What the server does at its edges: who may reach the internal endpoints, what headers protect answers, and how the FHIR API refuses what it does not speak.</summary>
public class ServerBoundaryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // ---------- Start-up ----------

    [Fact]
    public void A_port_already_in_use_is_explained_with_how_to_pick_another()
    {
        var failure = new IOException("Failed to bind to address http://127.0.0.1:5020: address already in use.",
            new AddressInUseException("Address already in use", new SocketException((int)SocketError.AddressAlreadyInUse)));

        var message = PortProblem.Explain(failure);

        Assert.NotNull(message);
        Assert.Contains("http://127.0.0.1:5020", message);
        Assert.Contains("--urls http://localhost:", message);
        Assert.Contains("--PlanApi", message);
        Assert.Contains("excludedportrange", message);
    }

    [Fact]
    public void A_port_Windows_has_reserved_is_explained_too()
    {
        var message = PortProblem.Explain(new SocketException((int)SocketError.AccessDenied));

        Assert.NotNull(message);
        Assert.Contains("reserved", message);
    }

    [Fact]
    public void Other_start_up_failures_are_left_as_they_are()
    {
        Assert.Null(PortProblem.Explain(new InvalidOperationException("The service list is broken.")));
    }

    // ---------- Internal endpoints ----------

    [Fact]
    public async Task The_review_queue_needs_a_staff_sign_in()
    {
        var response = await factory.Anonymous().GetAsync("/internal/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_portal_sends_someone_not_signed_in_to_sign_in()
    {
        var response = await factory.Anonymous().GetAsync("/requests/anything");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/staff/dev-sign-in", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task An_admin_cannot_see_the_review_queue()
    {
        var response = await factory.SignedInAs(ApiFactory.AdminAccount).GetAsync("/internal/requests");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Someone_who_works_the_queue_cannot_register_provider_systems()
    {
        var response = await factory.SignedInAs(ApiFactory.ClinicianAccount).GetAsync("/internal/clients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task There_is_no_test_user_sign_in_for_an_account_that_is_not_configured()
    {
        var response = await factory.Anonymous().PostAsync("/staff/dev-sign-in",
            new FormUrlEncodedContent([new("account", @"COUNTY\someone.real"), new("returnUrl", "/")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_user_sign_in_never_redirects_to_another_site()
    {
        var response = await factory.Anonymous().PostAsync("/staff/dev-sign-in",
            new FormUrlEncodedContent([new("account", ApiFactory.ClinicianAccount), new("returnUrl", "//attacker.example/")]));

        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    [Fact]
    public void An_out_of_date_local_database_file_stops_start_up_with_the_fix()
    {
        if (Environment.GetEnvironmentVariable("PRIORAUTH_TEST_SQLSERVER") is not null) return; // a SQLite-only check
        var path = Path.Combine(Path.GetTempPath(), $"priorauth-stale-{Guid.NewGuid():N}.db");
        try
        {
            // A file made by an older build: it has a requests table, without the columns added since.
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                old.Open();
                using var create = old.CreateCommand();
                create.CommandText = "CREATE TABLE AuthorizationRequests (Id TEXT PRIMARY KEY)";
                create.ExecuteNonQuery();
            }
            using var stale = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:PriorAuth", $"Data Source={path};Pooling=False"));

            var refused = Assert.ThrowsAny<Exception>(() => stale.CreateClient());

            var message = (refused.InnerException ?? refused).Message;
            Assert.Contains("was made by an older version", message);
            Assert.Contains("AuthorizationRequests.", message);
            Assert.Contains("more", message);
            Assert.Contains("delete that file", message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_request_addressed_to_another_host_name_is_refused()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/requests");
        request.Headers.Host = "attacker.example";

        Assert.NotEqual(HttpStatusCode.OK, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task An_internal_change_without_the_portal_header_is_refused()
    {
        var bare = factory.CreateClient();
        bare.DefaultRequestHeaders.Remove(PriorAuth.Api.Auth.AuthSetup.InternalRequestHeader);

        var response = await bare.PostAsync("/internal/clients/anything/deactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Headers that protect answers ----------

    [Fact]
    public async Task Answers_are_never_cached_and_the_portal_cannot_be_framed()
    {
        var client = factory.CreateClient();

        var queue = await client.GetAsync("/internal/requests");
        var page = await client.GetAsync("/");

        Assert.True(queue.Headers.CacheControl?.NoStore);
        Assert.True(page.Headers.CacheControl?.NoStore);
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
