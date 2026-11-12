using Hl7.Fhir.Model;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Tests;

/// <summary>
/// How the plan's service list decides what a coded service is and which review rule applies.
/// CRD, DTR and PAS all ask the list the same question, so these rules hold everywhere.
/// </summary>
public class ServiceRuleTests
{
    private static readonly QuestionnaireDefinition Residential = new() { Id = "dtr-residential", Title = "Residential" };
    private static readonly QuestionnaireDefinition DayTreatment = new() { Id = "dtr-day-treatment", Title = "Day treatment" };

    private static ServiceRequirement Service(string code, ReviewRule rule, string? questionnaire = null, string system = CodeSystems.Hcpcs, params string[] modifiers) =>
        new() { Code = code, System = system, Modifiers = modifiers, Name = $"Service {code} {string.Join(":", modifiers)}".Trim(), Rule = rule, Questionnaire = questionnaire };

    private static ServiceRequirementCatalog Catalog(params ServiceRequirement[] services) => new(services, [Residential, DayTreatment]);

    private static readonly ServiceRequirementCatalog H0019 = Catalog(
        Service("H0019", ReviewRule.NoPriorAuthorization),
        Service("H0019", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: "U1"),
        Service("H0019", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: "U2"));

    [Fact]
    public void A_level_modifier_on_the_request_picks_that_level()
    {
        var match = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: ["U2"]);

        Assert.Equal(MatchKind.Listed, match.Kind);
        Assert.Equal(ReviewRule.PriorAuthorization, match.Requirement!.Rule);
        Assert.Equal(["U2"], match.Requirement.Modifiers);
    }

    [Fact]
    public void A_request_with_no_modifiers_matches_the_entry_that_needs_none_when_no_other_entry_is_reviewed()
    {
        var catalog = Catalog(
            Service("H2012", ReviewRule.PriorAuthorization, "dtr-day-treatment"),
            Service("H2012", ReviewRule.PriorAuthorization, "dtr-day-treatment", modifiers: "TG"));

        var match = catalog.Match([new(CodeSystems.Hcpcs, "H2012")], modifiers: []);

        Assert.Equal(MatchKind.Listed, match.Kind);
        Assert.Empty(match.Requirement!.Modifiers);
    }

    [Fact]
    public void Falling_back_to_a_no_review_entry_while_a_sibling_needs_review_is_ambiguous_not_exempt()
    {
        // H0019 with no level modifier may be adult residential, or a DMC-ODS residential request missing its modifier.
        var match = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: []);

        Assert.Equal(MatchKind.Ambiguous, match.Kind);
    }

    [Fact]
    public void Two_equally_specific_entries_that_both_fit_are_ambiguous()
    {
        var match = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: ["U1", "U2"]);

        Assert.Equal(MatchKind.Ambiguous, match.Kind);
    }

    [Fact]
    public void A_reviewed_code_sent_without_the_modifier_its_entry_needs_is_ambiguous_not_exempt()
    {
        var catalog = Catalog(Service("S5145", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: "HE"));

        Assert.Equal(MatchKind.Ambiguous, catalog.Match([new(CodeSystems.Hcpcs, "S5145")], modifiers: []).Kind);
    }

    [Fact]
    public void Codes_and_modifiers_in_the_list_are_trimmed_like_the_ones_sent()
    {
        var catalog = Catalog(Service(" H0019 ", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: " u1 "));

        Assert.Equal(MatchKind.Listed, catalog.Match([new(CodeSystems.Hcpcs, "H0019")], ["U1"]).Kind);
    }

    [Fact]
    public void Modifiers_the_list_does_not_use_do_not_stop_a_match()
    {
        var match = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: ["HE", "U1"]);

        Assert.Equal(["U1"], match.Requirement!.Modifiers);
    }

    [Fact]
    public void When_modifiers_are_unknown_and_the_entries_disagree_the_answer_is_ambiguous()
    {
        var match = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: null);

        Assert.Equal(MatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Requirement);
        Assert.Equal(3, match.Candidates.Count);
    }

    [Fact]
    public void When_modifiers_are_unknown_but_every_entry_has_the_same_rule_and_questionnaire_the_answer_is_definite()
    {
        var catalog = Catalog(
            Service("H2012", ReviewRule.PriorAuthorization, "dtr-day-treatment"),
            Service("H2012", ReviewRule.PriorAuthorization, "dtr-day-treatment", modifiers: "TG"));

        var match = catalog.Match([new(CodeSystems.Hcpcs, "H2012")], modifiers: null);

        Assert.Equal(MatchKind.Listed, match.Kind);
        Assert.Equal("dtr-day-treatment", match.Requirement!.Questionnaire);
    }

    [Fact]
    public void An_entry_that_needs_a_modifier_is_still_found_when_modifiers_are_unknown()
    {
        var catalog = Catalog(Service("S5145", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: "HE"));

        Assert.Equal(MatchKind.Listed, catalog.Match([new(CodeSystems.Hcpcs, "S5145")], modifiers: null).Kind);
    }

    [Fact]
    public void A_no_review_code_sent_without_the_modifier_its_entry_needs_is_not_listed()
    {
        var catalog = Catalog(Service("H0019", ReviewRule.NoPriorAuthorization, modifiers: "HB"));

        Assert.Equal(MatchKind.NotListed, catalog.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: []).Kind);
    }

    [Fact]
    public void A_code_only_matches_in_its_own_code_system()
    {
        var catalog = Catalog(Service("90870", ReviewRule.NoPriorAuthorization, system: CodeSystems.Cpt));

        Assert.Equal(MatchKind.Listed, catalog.Match([new(CodeSystems.Cpt, "90870")], []).Kind);
        Assert.Equal(MatchKind.NotListed, catalog.Match([new(CodeSystems.Hcpcs, "90870")], []).Kind);
    }

    [Fact]
    public void The_first_listed_coding_decides_the_service_wherever_it_sits()
    {
        var match = H0019.Match([new(CodeSystems.Cpt, "90834"), new(CodeSystems.Hcpcs, "H0019")], ["U1"]);

        Assert.Equal("H0019", match.Coding!.Code);
        Assert.Equal(MatchKind.Listed, match.Kind);
    }

    [Theory]
    [InlineData(CodeSystems.Cpt, "90834")]
    [InlineData(CodeSystems.Hcpcs, "H2017")]
    [InlineData(CodeSystems.RevenueCode, "0114")]
    public void A_well_formed_code_in_a_known_system_that_is_not_listed_is_not_listed(string system, string code)
    {
        Assert.Equal(MatchKind.NotListed, H0019.Match([new(system, code)], []).Kind);
    }

    [Theory]
    [InlineData("http://example.org/local-codes", "H0019")]
    [InlineData(CodeSystems.Hcpcs, "H00")]
    [InlineData(CodeSystems.Hcpcs, "H0018-33")]
    [InlineData(CodeSystems.Cpt, "ABCDE")]
    [InlineData(CodeSystems.RevenueCode, "100")]
    [InlineData(null, "H0019")]
    public void A_code_that_cannot_be_recognised_is_unrecognised_not_exempt(string? system, string code)
    {
        Assert.Equal(MatchKind.Unrecognised, H0019.Match([new(system, code)], []).Kind);
    }

    [Fact]
    public void No_coding_at_all_is_unrecognised()
    {
        Assert.Equal(MatchKind.Unrecognised, H0019.Match([], []).Kind);
        Assert.Equal(MatchKind.Unrecognised, H0019.Match(null, null).Kind);
    }

    [Fact]
    public void Codes_are_compared_without_regard_to_case_or_surrounding_spaces()
    {
        Assert.Equal(MatchKind.Listed, H0019.Match([new(CodeSystems.Hcpcs, " h0019 ")], [" u1 "]).Kind);
    }

    // ---------- What stops the service from starting ----------

    [Fact]
    public void The_same_code_and_modifiers_twice_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog(
            Service("H0019", ReviewRule.PriorAuthorization, "dtr-residential", modifiers: "U1"),
            Service("h0019", ReviewRule.NoPriorAuthorization, modifiers: "u1")));

        Assert.Contains("listed more than once", error.Message);
    }

    [Fact]
    public void A_service_needing_review_without_a_questionnaire_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog(Service("H2019", ReviewRule.PriorAuthorization)));

        Assert.Contains("needs a Questionnaire", error.Message);
    }

    [Fact]
    public void A_service_pointing_at_a_questionnaire_that_does_not_exist_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog(Service("H2019", ReviewRule.PriorAuthorization, "dtr-missing")));

        Assert.Contains("dtr-missing", error.Message);
    }

    [Fact]
    public void A_service_that_needs_no_review_cannot_have_a_questionnaire()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog(Service("90870", ReviewRule.NoPriorAuthorization, "dtr-residential", system: CodeSystems.Cpt)));

        Assert.Contains("needs no review", error.Message);
    }

    [Fact]
    public void A_code_that_is_not_well_formed_for_its_system_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog(Service("H0018-33", ReviewRule.PriorAuthorization, "dtr-residential")));

        Assert.Contains("H0018-33", error.Message);
    }

    [Fact]
    public void A_questionnaire_id_that_does_not_start_with_a_letter_stops_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceRequirementCatalog([], [new QuestionnaireDefinition { Id = "1-residential", Title = "R" }]));

        Assert.Contains("1-residential", error.Message);
    }

    [Fact]
    public void A_submitted_service_that_is_ambiguous_is_pended_for_a_person_with_a_note_naming_the_entries()
    {
        var (review, note) = H0019.Match([new(CodeSystems.Hcpcs, "H0019")], modifiers: []).ReviewForRequest();

        Assert.Equal(ReviewType.PriorAuthorization, review);
        Assert.Contains("H0019:U1", note);
    }

    [Fact]
    public void The_shared_questionnaire_is_found_by_its_id()
    {
        var catalog = Catalog(Service("H2012", ReviewRule.PriorAuthorization, "dtr-day-treatment"));

        Assert.Equal("Day treatment", catalog.FindQuestionnaire("DTR-DAY-TREATMENT")?.Title);
        Assert.Null(catalog.FindQuestionnaire("dtr-missing"));
    }
}
