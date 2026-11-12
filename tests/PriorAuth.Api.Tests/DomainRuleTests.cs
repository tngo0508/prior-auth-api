using Microsoft.Extensions.Configuration;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Tests;

/// <summary>Rules that hold without a web server: the service list's own checks, matching, and the plan's calendar.</summary>
public class DomainRuleTests
{
    private static ServiceRequirementCatalog WithQuestion(RequirementQuestion question) => new(
        [new ServiceRequirement { Code = "H2019", Name = "TBS", Rule = ReviewRule.PriorAuthorization, Questionnaire = "dtr-q" }],
        [new QuestionnaireDefinition { Id = "dtr-q", Title = "Q", Questions = [question] }]);

    [Fact]
    public void Choice_options_whose_codes_would_collide_stop_start_up()
    {
        var question = new RequirementQuestion { LinkId = "q", Text = "Q", Type = "choice", Options = ["<5", ">5"] };

        var error = Assert.Throws<InvalidOperationException>(() => WithQuestion(question));

        Assert.Contains("share the code", error.Message);
    }

    [Fact]
    public void An_unknown_question_type_stops_start_up()
    {
        var question = new RequirementQuestion { LinkId = "q", Text = "Q", Type = "number" };

        Assert.Throws<InvalidOperationException>(() => WithQuestion(question));
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
    public void The_response_issue_date_is_the_plans_date_of_the_decision()
    {
        var clock = new PlanClock(new FixedTime(new DateTimeOffset(2026, 11, 12, 1, 30, 0, TimeSpan.Zero)), new ConfigurationBuilder().Build());
        var request = Pended();
        request.Approve(new DateOnly(2026, 11, 30), "PA-1", "Reviewer", clock.UtcNow, clock.Today);
        var claim = new Hl7.Fhir.Model.Claim { Item = [new() { Sequence = 1 }] };

        var response = PriorAuth.Api.Fhir.PasMapper.ToClaimResponse(request, claim, "https://plan.example", clock);

        var issued = response.Item[0].Extension.Single(e => e.Url == PriorAuth.Api.Fhir.PasMapper.ItemPreAuthIssueDateUrl).Value as Hl7.Fhir.Model.Date;
        Assert.Equal("2026-11-11", issued?.Value);
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
        ReviewType.PriorAuthorization, note: null, "{}", [], DateTime.UtcNow);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
