using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Tests;

/// <summary>Who on the plan's staff may do what to a request. No web server.</summary>
public class StaffRuleTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static readonly DateOnly Later = new(2027, 2, 4);

    private static readonly Staff Coordinator = Person(@"DEV\casey", "Casey Coordinator", StaffRole.Coordinator);
    private static readonly Staff Clinician = Person(@"DEV\lee", "Lee Clinician", StaffRole.LicensedClinician);
    private static readonly Staff OtherClinician = Person(@"DEV\sam", "Sam Clinician", StaffRole.LicensedClinician);
    private static readonly Staff Physician = Person(@"DEV\pat", "Dr Pat Physician", StaffRole.Physician);
    private static readonly Staff Admin = Person(@"DEV\ari", "Ari Admin", StaffRole.Admin);

    [Fact]
    public void A_coordinator_cannot_approve_or_deny()
    {
        var request = Pended();

        Assert.Throws<DomainRuleException>(() => request.Approve(Later, "PA-1", Coordinator, Now, Today));
        Assert.Throws<DomainRuleException>(() => request.Deny("not-medically-necessary", "Assessment does not support it.", Coordinator, Now));
        Assert.Throws<DomainRuleException>(() => request.ApproveWithChanges(Later, "6 hours a week, not 10", "PA-1", Coordinator, Now, Today));
    }

    [Fact]
    public void A_coordinator_can_ask_for_more_information()
    {
        var request = Pended();

        request.RequestInformation("Client plan", Coordinator, Now);

        Assert.Equal(AuthorizationStatus.InformationRequested, request.Status);
    }

    [Fact]
    public void A_licensed_clinician_decides_prior_authorization_and_is_recorded_by_account()
    {
        var request = Pended();

        request.Approve(Later, "PA-1", Clinician, Now, Today);

        Assert.Equal(AuthorizationStatus.Approved, request.Status);
        Assert.Equal("Lee Clinician", request.DecidedBy);
        Assert.Equal(@"DEV\lee", request.DecidedByAccount);
    }

    [Fact]
    public void Only_a_physician_decides_a_concurrent_review()
    {
        var request = Pended(ReviewType.ConcurrentReview);

        var refused = Assert.Throws<DomainRuleException>(() => request.Approve(Later, "PA-1", Clinician, Now, Today));
        request.Approve(Later, "PA-1", Physician, Now, Today);

        Assert.Contains("physician", refused.Message);
        Assert.Equal(AuthorizationStatus.Approved, request.Status);
    }

    [Fact]
    public void An_admin_cannot_work_the_queue()
    {
        var request = Pended();

        Assert.Throws<DomainRuleException>(() => request.Claim(Admin, Now));
        Assert.Throws<DomainRuleException>(() => request.RequestInformation("Client plan", Admin, Now));
    }

    [Fact]
    public void A_request_claimed_by_someone_else_cannot_be_decided()
    {
        var request = Pended();
        request.Claim(Clinician, Now);

        var refused = Assert.Throws<DomainRuleException>(() => request.Deny("not-medically-necessary", "No.", OtherClinician, Now));

        Assert.Contains("Lee Clinician", refused.Message);
        Assert.Throws<DomainRuleException>(() => request.Claim(OtherClinician, Now));
    }

    [Fact]
    public void The_person_who_claimed_a_request_can_decide_it()
    {
        var request = Pended();
        request.Claim(Clinician, Now);

        request.Deny("not-medically-necessary", "Assessment does not support it.", Clinician, Now);

        Assert.Equal(AuthorizationStatus.Denied, request.Status);
        Assert.Equal("not-medically-necessary", request.DenialReasonCode);
        Assert.Equal("Assessment does not support it.", request.Reason);
    }

    [Fact]
    public void Only_a_coordinator_assigns_and_only_to_someone_who_works_the_queue()
    {
        var request = Pended();

        Assert.Throws<DomainRuleException>(() => request.AssignTo(OtherClinician, Clinician, Now));
        Assert.Throws<DomainRuleException>(() => request.AssignTo(Admin, Coordinator, Now));
        request.AssignTo(Clinician, Coordinator, Now);

        Assert.Equal(@"DEV\lee", request.AssignedTo);
        Assert.Equal("Lee Clinician", request.AssignedToName);
    }

    [Fact]
    public void A_claim_is_released_by_its_holder_or_a_coordinator_only()
    {
        var request = Pended();
        request.Claim(Clinician, Now);

        Assert.Throws<DomainRuleException>(() => request.Release(OtherClinician, Now));
        request.Release(Coordinator, Now);

        Assert.Null(request.AssignedTo);
    }

    [Fact]
    public void Approving_with_changes_records_the_change_and_needs_one()
    {
        var request = Pended();

        Assert.Throws<DomainRuleException>(() => request.ApproveWithChanges(Later, "  ", "PA-1", Clinician, Now, Today));
        request.ApproveWithChanges(Later, "6 hours a week, not 10", "PA-1", Clinician, Now, Today);

        Assert.Equal(AuthorizationStatus.Approved, request.Status);
        Assert.Equal("6 hours a week, not 10", request.Modification);
    }

    [Fact]
    public void A_denial_needs_a_reason_code_and_text()
    {
        var request = Pended();

        Assert.Throws<DomainRuleException>(() => request.Deny("", "Text", Clinician, Now));
        Assert.Throws<DomainRuleException>(() => request.Deny("other", " ", Clinician, Now));
    }

    [Fact]
    public void A_decision_releases_the_claim()
    {
        var request = Pended();
        request.Claim(Clinician, Now);

        request.Approve(Later, "PA-1", Clinician, Now, Today);

        Assert.Null(request.AssignedTo);
    }

    [Fact]
    public void What_the_page_offers_matches_what_the_rules_allow()
    {
        var request = Pended(ReviewType.ConcurrentReview);
        request.Claim(Clinician, Now);

        Assert.False(request.MayBeDecidedBy(Clinician)); // a concurrent review is for a physician
        Assert.False(request.MayBeDecidedBy(Physician)); // Lee holds it
        Assert.False(request.MayBeClaimedBy(Physician));
        Assert.True(request.MayBeReleasedBy(Coordinator));
        Assert.False(request.MayBeReleasedBy(OtherClinician));
        Assert.True(request.MayBeAssignedBy(Coordinator));
        Assert.False(request.MayBeAssignedBy(Clinician));

        request.RequestInformation("Client plan", Clinician, Now);

        // Still open while waiting for the provider, so the holder may ask again, and the rule agrees.
        Assert.True(request.MayBeAskedForInformationBy(Clinician));
        request.RequestInformation("Client plan and the latest assessment", Clinician, Now);
        Assert.False(request.MayBeAskedForInformationBy(Admin));
    }

    private static Staff Person(string account, string name, params StaffRole[] roles) => new(account, name, roles.ToHashSet());

    private static AuthorizationRequest Pended(ReviewType review = ReviewType.PriorAuthorization) => AuthorizationRequest.Receive(
        new RequestDetails("client", "", "REQ-1", "Clinic", "Alex Testpatient", "sys|1", CodeSystems.Hcpcs, "H2019", "TBS"),
        review, note: null, "{}", [], Now);
}
