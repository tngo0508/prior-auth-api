using Microsoft.Extensions.Configuration;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Tests;

/// <summary>Rules that hold without a web server: the service list's own checks, matching, and the plan's calendar.</summary>
public class DomainRuleTests
{
    private static ServiceRequirement Service(string code, string system = CodeSystems.Hcpcs, params RequirementQuestion[] questions) =>
        new() { Code = code, System = system, Name = "Service " + code, Questions = questions };

    [Fact]
    public void A_service_list_with_the_same_code_twice_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceRequirementCatalog([Service("H2019"), Service("h2019")]));

        Assert.Contains("listed more than once", error.Message);
    }

    [Fact]
    public void Choice_options_whose_codes_would_collide_stop_start_up()
    {
        var question = new RequirementQuestion { LinkId = "q", Text = "Q", Type = "choice", Options = ["<5", ">5"] };

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceRequirementCatalog([Service("H2019", questions: question)]));

        Assert.Contains("share the code", error.Message);
    }

    [Fact]
    public void An_unknown_question_type_stops_start_up()
    {
        var question = new RequirementQuestion { LinkId = "q", Text = "Q", Type = "number" };

        Assert.Throws<InvalidOperationException>(() => new ServiceRequirementCatalog([Service("H2019", questions: question)]));
    }

    [Fact]
    public void A_code_only_matches_in_its_own_code_system()
    {
        var catalog = new ServiceRequirementCatalog([Service("H2019")]);

        Assert.NotNull(catalog.Find(CodeSystems.Hcpcs, "H2019"));
        Assert.Null(catalog.Find("http://example.org/local-codes", "H2019"));
        Assert.Null(catalog.Find(null, "H2019"));
    }

    [Fact]
    public void The_first_listed_coding_decides_the_service_wherever_it_sits()
    {
        var catalog = new ServiceRequirementCatalog([Service("H2019")]);

        var (requirement, coding) = catalog.Match([new(CodeSystems.Cpt, "90834"), new(CodeSystems.Hcpcs, "H2019")]);

        Assert.Equal("H2019", requirement?.Code);
        Assert.Equal("H2019", coding?.Code);
    }

    [Fact]
    public void Today_is_the_plans_local_date_not_the_UTC_date()
    {
        // 01:30 UTC on 12 November is 17:30 on 11 November in California.
        var clock = new PlanClock(new FixedTime(new DateTimeOffset(2026, 11, 12, 1, 30, 0, TimeSpan.Zero)), new ConfigurationBuilder().Build());

        Assert.Equal(new DateOnly(2026, 11, 11), clock.Today);
    }

    [Fact]
    public void An_approval_through_the_plans_today_is_allowed_in_the_evening()
    {
        var request = Pended();

        request.Approve(new DateOnly(2026, 11, 11), "PA-1", "Reviewer", new DateTime(2026, 11, 12, 1, 30, 0, DateTimeKind.Utc), today: new DateOnly(2026, 11, 11));

        Assert.Equal(AuthorizationStatus.Approved, request.Status);
    }

    [Fact]
    public void A_denial_carries_no_authorization_number_or_end_date()
    {
        var request = Pended();

        request.Deny("Not medically necessary", "Reviewer", DateTime.UtcNow);

        Assert.Null(request.AuthorizationNumber);
        Assert.Null(request.AuthorizedThrough);
    }

    [Fact]
    public void Every_change_moves_the_version_on()
    {
        var request = Pended();
        var before = request.Version;

        request.RequestInformation("Client plan", "Reviewer", DateTime.UtcNow);

        Assert.True(request.Version > before);
    }

    private static AuthorizationRequest Pended() => AuthorizationRequest.Receive(
        new RequestDetails("client", "", "REQ-1", "Clinic", "Alex Testpatient", "sys|1", CodeSystems.Hcpcs, "H2019", "TBS"),
        authorizationRequired: true, "{}", [], DateTime.UtcNow);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
