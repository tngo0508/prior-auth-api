using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PriorAuth.Api.Tests;

public class CrdAndDtrTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OrderSign = "/cds-services/prior-auth-order-sign";
    private const string OrderSelect = "/cds-services/prior-auth-order-select";
    private const string CoverageInformation = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";
    private const string CrdTemp = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";
    private const string QrContext = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-context";
    private const string IntendedUse = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/intendedUse";
    private const string Package = "/fhir/Questionnaire/$questionnaire-package";
    private const string H2019Canonical = "http://localhost/fhir/Questionnaire/dtr-mhp-youth";

    private TestProvider _provider = null!;
    private HttpClient _signedIn = null!;
    private readonly HttpClient _anonymous = factory.CreateClient();

    public async Task InitializeAsync()
    {
        _provider = await TestProvider.RegisterAsync(factory.CreateClient());
        _signedIn = await _provider.SignInAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- CRD: discovery ----------

    [Fact]
    public async Task Discovery_lists_the_order_sign_and_order_select_services_without_a_token()
    {
        var discovery = await _anonymous.GetFromJsonAsync<JsonElement>("/cds-services");

        var hooks = discovery.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("hook").GetString()).ToArray();
        Assert.Equal(["order-sign", "order-select"], hooks);
    }

    [Fact]
    public async Task Each_discovery_entry_offers_a_boolean_coverage_info_configuration_option_that_defaults_to_on()
    {
        var discovery = await _anonymous.GetFromJsonAsync<JsonElement>("/cds-services");

        foreach (var service in discovery.GetProperty("services").EnumerateArray())
        {
            var options = service.GetProperty("extension").GetProperty("davinci-crd.configuration-options");
            var coverageInfo = options.EnumerateArray().Single(o => o.GetProperty("code").GetString() == "coverage-info");
            Assert.Equal("boolean", coverageInfo.GetProperty("type").GetString());
            Assert.True(coverageInfo.GetProperty("default").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(coverageInfo.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(coverageInfo.GetProperty("description").GetString()));
        }
    }

    // ---------- CRD: is authorization needed? ----------

    [Fact]
    public async Task An_order_for_a_listed_service_comes_back_marked_as_needing_authorization_with_its_questionnaire()
    {
        var response = await CallHook(OrderSign, Hook("order-sign", Order("H2019", coverage: "Coverage/cov-1")));
        var action = Assert.Single(response.GetProperty("systemActions").EnumerateArray());
        var information = CoverageInfo(action.GetProperty("resource"));

        Assert.Equal("update", action.GetProperty("type").GetString());
        Assert.Equal("ServiceRequest", action.GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("conditional", Value(information, "covered", "valueCode"));
        Assert.Equal("auth-needed", Value(information, "pa-needed", "valueCode"));
        Assert.Equal("clinical", Value(information, "doc-needed", "valueCode"));
        Assert.Equal("withpa", Value(information, "doc-purpose", "valueCode"));
        Assert.EndsWith("/fhir/Questionnaire/dtr-mhp-youth", Value(information, "questionnaire", "valueCanonical"));
        Assert.Equal("Coverage/cov-1", Sub(information, "coverage").GetProperty("valueReference").GetProperty("reference").GetString());
        Assert.False(string.IsNullOrEmpty(Value(information, "date", "valueDate")));
        Assert.False(string.IsNullOrEmpty(Value(information, "coverage-assertion-id", "valueString")));
    }

    [Fact]
    public async Task An_order_for_an_unlisted_service_comes_back_as_needing_no_authorization_and_without_claiming_coverage()
    {
        var response = await CallHook(OrderSign, Hook("order-sign", Order("90834", coverage: "Coverage/cov-1")));
        var information = CoverageInfo(Assert.Single(response.GetProperty("systemActions").EnumerateArray()).GetProperty("resource"));

        Assert.Equal("no-auth", Value(information, "pa-needed", "valueCode"));
        Assert.Equal("conditional", Value(information, "covered", "valueCode"));
        Assert.False(HasSub(information, "questionnaire"));
        Assert.False(HasSub(information, "doc-purpose"));
    }

    [Fact]
    public async Task An_inpatient_admission_needs_no_prior_authorization_and_is_told_to_send_it_for_concurrent_review()
    {
        var information = await AnswerFor(Order("0100", "Coverage/cov-1", system: RevenueCodes));

        Assert.Equal("no-auth", Value(information, "pa-needed", "valueCode"));
        Assert.False(HasSub(information, "questionnaire"));
        var reason = Sub(information, "reason").GetProperty("valueCodeableConcept").GetProperty("text").GetString();
        Assert.Contains("reviewed concurrently", reason);
        Assert.Contains("24 hours", reason);
        Assert.Contains("/fhir/Questionnaire/dtr-mhp-inpatient", reason);
    }

    [Fact]
    public async Task A_listed_service_the_plan_may_not_prior_authorize_says_so()
    {
        var information = await AnswerFor(Order("H0018", "Coverage/cov-1"));

        Assert.Equal("no-auth", Value(information, "pa-needed", "valueCode"));
        Assert.Contains("may not require prior authorization", Sub(information, "reason").GetProperty("valueCodeableConcept").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_code_whose_entries_differ_only_by_modifier_and_rule_is_answered_conditional()
    {
        // H0019 is adult residential (no prior authorization) or, with U1, U2 or U3, a DMC-ODS residential level (prior authorization).
        var information = await AnswerFor(Order("H0019", "Coverage/cov-1"));

        Assert.Equal("conditional", Value(information, "pa-needed", "valueCode"));
        Assert.False(HasSub(information, "questionnaire"));
        Assert.Contains("H0019:U1", Sub(information, "reason").GetProperty("valueCodeableConcept").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_code_whose_entries_share_a_rule_and_questionnaire_is_answered_without_modifiers()
    {
        // H2012 is day rehabilitation or, with TG, day treatment intensive. Both need authorization with the same questionnaire.
        var information = await AnswerFor(Order("H2012", "Coverage/cov-1"));

        Assert.Equal("auth-needed", Value(information, "pa-needed", "valueCode"));
        Assert.EndsWith("/fhir/Questionnaire/dtr-mhp-day-treatment", Value(information, "questionnaire", "valueCanonical"));
    }

    [Fact]
    public async Task A_code_in_an_unknown_code_system_is_answered_conditional_not_no_auth()
    {
        var information = await AnswerFor(Order("H2019", "Coverage/cov-1", system: "http://example.org/local-codes"));

        Assert.Equal("conditional", Value(information, "pa-needed", "valueCode"));
        Assert.Contains("not recognised", Sub(information, "reason").GetProperty("valueCodeableConcept").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Coverage_sent_ahead_by_the_EHR_is_used_when_the_order_names_none()
    {
        var prefetch = """{ "coverage": { "resourceType": "Bundle", "type": "searchset", "entry": [{ "resource": { "resourceType": "Coverage", "id": "prefetched-1" } }] } }""";

        var response = await CallHook(OrderSign, Hook("order-sign", Order("H2019", coverage: null), prefetch: prefetch));
        var information = CoverageInfo(Assert.Single(response.GetProperty("systemActions").EnumerateArray()).GetProperty("resource"));

        Assert.Equal("Coverage/prefetched-1", Sub(information, "coverage").GetProperty("valueReference").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task An_order_with_no_coverage_at_all_gets_an_info_card_that_names_the_order_and_its_topic()
    {
        var response = await CallHook(OrderSign, Hook("order-sign", Order("H2019", coverage: null)));
        var card = Assert.Single(response.GetProperty("cards").EnumerateArray());
        var topic = card.GetProperty("source").GetProperty("topic");

        Assert.False(response.TryGetProperty("systemActions", out _));
        Assert.Equal("info", card.GetProperty("indicator").GetString());
        Assert.Equal("Behavioral Health Plan (synthetic)", card.GetProperty("source").GetProperty("label").GetString());
        Assert.Equal(CrdTemp, topic.GetProperty("system").GetString());
        Assert.Equal("coverage-info", topic.GetProperty("code").GetString());
        Assert.InRange(card.GetProperty("summary").GetString()!.Length, 1, 139);
        Assert.Contains("ServiceRequest/order-1", card.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_order_missing_authoredOn_or_requester_gets_an_explanation_instead_of_an_annotation()
    {
        var response = await CallHook(OrderSign, Hook("order-sign", Order("H2019", "Coverage/cov-1", conformant: false)));
        var card = Assert.Single(response.GetProperty("cards").EnumerateArray());

        Assert.False(response.TryGetProperty("systemActions", out _));
        Assert.Equal("info", card.GetProperty("indicator").GetString());
        Assert.Contains("authoredOn", card.GetProperty("detail").GetString());
        Assert.Contains("ServiceRequest/order-1", card.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Calling_again_with_the_order_already_annotated_returns_no_update()
    {
        var first = await CallHook(OrderSign, Hook("order-sign", Order("H2019", "Coverage/cov-1")));
        var annotated = first.GetProperty("systemActions")[0].GetProperty("resource").GetRawText();

        var second = await CallHook(OrderSign, Hook("order-sign", annotated));

        Assert.False(second.TryGetProperty("systemActions", out _));
        Assert.Empty(second.GetProperty("cards").EnumerateArray());
    }

    [Fact]
    public async Task Annotations_for_other_coverages_are_kept_and_the_changed_one_is_replaced()
    {
        var first = await CallHook(OrderSign, Hook("order-sign", Order("H2019", "Coverage/other")));
        var annotatedForOther = first.GetProperty("systemActions")[0].GetProperty("resource");
        var otherId = Value(CoverageInfo(annotatedForOther), "coverage-assertion-id", "valueString");
        // The same order, now sent with a different coverage first in its insurance list.
        var node = JsonNode.Parse(annotatedForOther.GetRawText())!;
        node["insurance"] = new JsonArray(new JsonObject { ["reference"] = "Coverage/cov-1" }, new JsonObject { ["reference"] = "Coverage/other" });
        var resent = node.ToJsonString();

        var second = await CallHook(OrderSign, Hook("order-sign", resent));
        var extensions = second.GetProperty("systemActions")[0].GetProperty("resource").GetProperty("extension").EnumerateArray()
            .Where(e => e.GetProperty("url").GetString() == CoverageInformation).ToList();

        Assert.Equal(2, extensions.Count);
        var kept = extensions.Single(e => Sub(e, "coverage").GetProperty("valueReference").GetProperty("reference").GetString() == "Coverage/other");
        var added = extensions.Single(e => Sub(e, "coverage").GetProperty("valueReference").GetProperty("reference").GetString() == "Coverage/cov-1");
        Assert.Equal(otherId, Value(kept, "coverage-assertion-id", "valueString"));
        Assert.NotEqual(otherId, Value(added, "coverage-assertion-id", "valueString"));
    }

    [Fact]
    public async Task Order_select_answers_only_the_selected_orders_and_never_asks_for_documentation()
    {
        var orders = new[] { Order("H2019", "Coverage/cov-1", id: "order-1"), Order("H2012", "Coverage/cov-1", id: "order-2") };

        var response = await CallHook(OrderSelect, Hook("order-select", orders, selections: ["ServiceRequest/order-2"]));
        var resource = Assert.Single(response.GetProperty("systemActions").EnumerateArray()).GetProperty("resource");
        var information = CoverageInfo(resource);

        Assert.Equal("order-2", resource.GetProperty("id").GetString());
        Assert.Equal("auth-needed", Value(information, "pa-needed", "valueCode"));
        Assert.False(HasSub(information, "doc-needed"));
        Assert.False(HasSub(information, "questionnaire"));
        Assert.False(HasSub(information, "info-needed"));
        Assert.False(HasSub(information, "doc-purpose"));
    }

    [Fact]
    public async Task Turning_coverage_info_off_in_the_request_configuration_suppresses_coverage_information()
    {
        var body = Hook("order-sign", Order("H2019", "Coverage/cov-1"), extension: """{ "davinci-crd.configuration": { "coverage-info": false } }""");

        var response = await CallHook(OrderSign, body);

        Assert.False(response.TryGetProperty("systemActions", out _));
        Assert.Empty(response.GetProperty("cards").EnumerateArray());
    }

    [Fact]
    public async Task Configuration_the_service_does_not_know_is_ignored()
    {
        var body = Hook("order-sign", Order("H2019", "Coverage/cov-1"), extension: """{ "davinci-crd.configuration": { "max-cards": 3 }, "other.extension": {} }""");

        var response = await CallHook(OrderSign, body);

        Assert.Single(response.GetProperty("systemActions").EnumerateArray());
    }

    [Fact]
    public async Task The_coverage_information_date_is_the_plans_local_date_not_the_UTC_date()
    {
        // Pick a time zone whose date differs from the UTC date right now, so the test fails if the UTC date is used.
        var zone = DateTime.UtcNow.Hour < 12 ? "Etc/GMT+12" : "Pacific/Kiritimati";
        using var shifted = factory.WithWebHostBuilder(b => b.UseSetting("Plan:TimeZone", zone));
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone))).ToString("yyyy-MM-dd");
        Assert.NotEqual(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), expected);

        var request = new HttpRequestMessage(HttpMethod.Post, OrderSign) { Content = Json(Hook("order-sign", Order("H2019", "Coverage/cov-1"))) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.Assertion(audience: "http://localhost" + OrderSign));
        var response = await shifted.CreateClient().SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(expected, Value(CoverageInfo(body.GetProperty("systemActions")[0].GetProperty("resource")), "date", "valueDate"));
    }

    [Fact]
    public async Task A_hook_call_without_a_signed_token_is_refused()
    {
        var response = await _anonymous.PostAsync(OrderSign, Json(Hook("order-sign", Order("H2019", "Coverage/cov-1"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_token_made_for_a_different_service_is_refused()
    {
        var response = await Post(OrderSign, Hook("order-sign", Order("H2019", "Coverage/cov-1")), audience: "http://localhost" + OrderSelect);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_call_for_the_wrong_hook_is_refused()
    {
        var response = await Post(OrderSign, Hook("patient-view", Order("H2019", "Coverage/cov-1")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_hook_call_that_is_not_JSON_is_refused()
    {
        var response = await Post(OrderSign, "this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private const string GoodOrders = """{ "resourceType": "Bundle", "type": "collection", "entry": [] }""";

    [Theory]
    [InlineData(OrderSign, """{ "hook": 1, "hookInstance": "x", "context": { "draftOrders": {{orders}} } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "context": { "draftOrders": {{orders}} } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": "x" }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": "x" } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "prefetch": "x" }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "prefetch": { "coverage": "x" } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "prefetch": { "coverage": { "resourceType": "Bundle", "entry": "x" } } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "prefetch": { "coverage": { "resourceType": "Bundle", "entry": ["x"] } } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "prefetch": { "coverage": { "resourceType": "Coverage", "id": 5 } } }""")]
    [InlineData(OrderSign, """{ "hook": "order-sign", "hookInstance": "x", "context": { "draftOrders": {{orders}} }, "extension": "x" }""")]
    [InlineData(OrderSelect, """{ "hook": "order-select", "hookInstance": "x", "context": { "draftOrders": {{orders}} } }""")]
    [InlineData(OrderSelect, """{ "hook": "order-select", "hookInstance": "x", "context": { "selections": "ServiceRequest/order-1", "draftOrders": {{orders}} } }""")]
    [InlineData(OrderSelect, """{ "hook": "order-select", "hookInstance": "x", "context": { "selections": [1], "draftOrders": {{orders}} } }""")]
    [InlineData(OrderSign, """[]""")]
    public async Task A_malformed_hook_call_is_refused_with_422_and_an_OperationOutcome(string path, string body)
    {
        var response = await Post(path, body.Replace("{{orders}}", GoodOrders));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("OperationOutcome", JsonDocument.Parse(text).RootElement.GetProperty("resourceType").GetString());
    }

    [Fact]
    public async Task A_null_prefetch_entry_means_the_EHR_had_no_data_and_is_accepted()
    {
        var response = await CallHook(OrderSign, Hook("order-sign", Order("H2019", "Coverage/cov-1"), prefetch: """{ "coverage": null }"""));

        Assert.Single(response.GetProperty("systemActions").EnumerateArray());
    }

    // ---------- DTR: what do you need to know? ----------

    [Fact]
    public async Task The_questionnaire_package_for_a_named_questionnaire_starts_with_the_questionnaire_and_includes_a_draft_response()
    {
        var parameters = Parameters(CoverageParameter(), $$"""{ "name": "questionnaire", "valueCanonical": "{{H2019Canonical}}|1" }""");

        var body = await PostPackage(parameters);
        var package = Assert.Single(body.GetProperty("parameter").EnumerateArray());
        var entries = package.GetProperty("resource").GetProperty("entry");

        Assert.Equal("PackageBundle", package.GetProperty("name").GetString());
        Assert.Equal("collection", package.GetProperty("resource").GetProperty("type").GetString());
        Assert.Equal("Questionnaire", entries[0].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("QuestionnaireResponse", entries[1].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("in-progress", entries[1].GetProperty("resource").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_draft_response_carries_the_DTR_profile_subject_authored_items_context_and_intended_use()
    {
        var parameters = Parameters(CoverageParameter(), $$"""{ "name": "order", "resource": {{Order("H2019", "Coverage/cov-1")}} }""");

        var body = await PostPackage(parameters);
        var draft = DraftResponse(body.GetProperty("parameter")[0]);
        var contexts = draft.GetProperty("extension").EnumerateArray().Where(e => e.GetProperty("url").GetString() == QrContext)
            .Select(e => e.GetProperty("valueReference").GetProperty("reference").GetString()!).ToArray();
        var intendedUse = draft.GetProperty("extension").EnumerateArray().Single(e => e.GetProperty("url").GetString() == IntendedUse)
            .GetProperty("valueCodeableConcept").GetProperty("coding")[0];

        Assert.Equal("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse", draft.GetProperty("meta").GetProperty("profile")[0].GetString());
        Assert.Equal("Patient/synthetic-1", draft.GetProperty("subject").GetProperty("reference").GetString());
        Assert.False(string.IsNullOrEmpty(draft.GetProperty("authored").GetString()));
        Assert.Equal($"{H2019Canonical}|1", draft.GetProperty("questionnaire").GetString());
        Assert.Equal(["Coverage/cov-1", "ServiceRequest/order-1"], contexts.Order().ToArray());
        Assert.Equal(CrdTemp, intendedUse.GetProperty("system").GetString());
        Assert.Equal("withpa", intendedUse.GetProperty("code").GetString());
        Assert.Contains(draft.GetProperty("item").EnumerateArray(), i => i.GetProperty("linkId").GetString() == "assessment-date");
    }

    [Fact]
    public async Task Asked_by_canonical_with_a_coverage_the_draft_response_subject_is_the_coverage_beneficiary()
    {
        var parameters = Parameters($$"""{ "name": "questionnaire", "valueCanonical": "{{H2019Canonical}}" }""", CoverageParameter(beneficiary: "Patient/from-coverage"));

        var draft = DraftResponse((await PostPackage(parameters)).GetProperty("parameter")[0]);

        Assert.Equal("Patient/from-coverage", draft.GetProperty("subject").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task The_package_does_not_depend_on_the_order_of_the_parameters()
    {
        var canonical = $$"""{ "name": "questionnaire", "valueCanonical": "{{H2019Canonical}}|1" }""";
        var order = $$"""{ "name": "order", "resource": {{Order("H2019", "Coverage/cov-1")}} }""";

        var canonicalFirst = DraftResponse((await PostPackage(Parameters(CoverageParameter(), canonical, order))).GetProperty("parameter")[0]);
        var orderFirst = DraftResponse((await PostPackage(Parameters(order, canonical, CoverageParameter()))).GetProperty("parameter")[0]);

        Assert.Equal("Patient/synthetic-1", canonicalFirst.GetProperty("subject").GetProperty("reference").GetString());
        Assert.Equal("Patient/synthetic-1", orderFirst.GetProperty("subject").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task The_questionnaire_package_can_be_chosen_from_the_order_and_carries_the_services_questions()
    {
        var parameters = Parameters(CoverageParameter(), $$"""{ "name": "order", "resource": {{Order("H2012", "Coverage/cov-1")}} }""");

        var body = await PostPackage(parameters);
        var questionnaire = body.GetProperty("parameter")[0].GetProperty("resource").GetProperty("entry")[0].GetProperty("resource");
        var schedule = questionnaire.GetProperty("item").EnumerateArray().Single(i => i.GetProperty("linkId").GetString() == "schedule");

        Assert.Equal("dtr-mhp-day-treatment", questionnaire.GetProperty("id").GetString());
        Assert.Equal("choice", schedule.GetProperty("type").GetString());
        Assert.True(schedule.GetProperty("required").GetBoolean());
        Assert.Equal(["Half day", "Full day"], schedule.GetProperty("answerOption").EnumerateArray().Select(o => o.GetProperty("valueCoding").GetProperty("display").GetString()!).ToArray());
    }

    [Fact]
    public async Task Two_services_that_share_a_questionnaire_get_one_package_naming_both_orders()
    {
        // TBS (H2019) and therapeutic foster care (S5145) are documented on the same questionnaire.
        var parameters = Parameters(CoverageParameter(),
            $$"""{ "name": "order", "resource": {{Order("H2019", "Coverage/cov-1", id: "order-tbs")}} }""",
            $$"""{ "name": "order", "resource": {{Order("S5145", "Coverage/cov-1", id: "order-tfc")}} }""");

        var body = await PostPackage(parameters);
        var package = Assert.Single(body.GetProperty("parameter").EnumerateArray(), p => p.GetProperty("name").GetString() == "PackageBundle");
        var questionnaire = package.GetProperty("resource").GetProperty("entry")[0].GetProperty("resource");
        var contexts = DraftResponse(package).GetProperty("extension").EnumerateArray().Where(e => e.GetProperty("url").GetString() == QrContext)
            .Select(e => e.GetProperty("valueReference").GetProperty("reference").GetString()!).ToArray();

        Assert.Equal("dtr-mhp-youth", questionnaire.GetProperty("id").GetString());
        Assert.Equal(["Coverage/cov-1", "ServiceRequest/order-tbs", "ServiceRequest/order-tfc"], contexts.Order().ToArray());
    }

    [Fact]
    public async Task An_order_whose_code_needs_a_modifier_to_decide_gets_no_questionnaire_and_a_warning_saying_why()
    {
        var parameters = Parameters(CoverageParameter(),
            $$"""{ "name": "order", "resource": {{Order("H2019", "Coverage/cov-1")}} }""",
            $$"""{ "name": "order", "resource": {{Order("H0019", "Coverage/cov-1", id: "order-2")}} }""");

        var body = await PostPackage(parameters);
        var issue = Assert.Single(body.GetProperty("parameter").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "operationOutcome")
            .GetProperty("resource").GetProperty("issue").EnumerateArray());

        Assert.Contains("ServiceRequest/order-2", issue.GetProperty("diagnostics").GetString());
        Assert.Contains("modifier", issue.GetProperty("diagnostics").GetString());
    }

    [Fact]
    public async Task A_draft_questionnaire_says_it_is_a_draft_and_lists_the_documents_to_send()
    {
        var questionnaire = await _signedIn.GetFromJsonAsync<JsonElement>("/fhir/Questionnaire/dtr-dmcods-residential");

        Assert.Equal("draft", questionnaire.GetProperty("status").GetString());
        Assert.Contains("ASAM assessment", questionnaire.GetProperty("description").GetString());
        Assert.Contains("Draft", questionnaire.GetProperty("text").GetProperty("div").GetString());
    }

    [Theory]
    [InlineData("http://localhost/fhir/Questionnaire/prior-auth-nope|1")]
    [InlineData("http://localhost/fhir/Questionnaire/dtr-mhp-youth|2")]
    [InlineData("https://elsewhere.example/fhir/Questionnaire/dtr-mhp-youth")]
    public async Task A_questionnaire_that_cannot_be_found_is_reported_as_a_warning_next_to_the_ones_that_can(string unknown)
    {
        var parameters = Parameters(CoverageParameter(),
            $$"""{ "name": "questionnaire", "valueCanonical": "{{unknown}}" }""",
            $$"""{ "name": "questionnaire", "valueCanonical": "http://localhost/fhir/Questionnaire/dtr-mhp-day-treatment|1" }""");

        var body = await PostPackage(parameters);
        var outcome = body.GetProperty("parameter").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "operationOutcome").GetProperty("resource");
        var package = Assert.Single(body.GetProperty("parameter").EnumerateArray(), p => p.GetProperty("name").GetString() == "PackageBundle");

        Assert.Equal("dtr-mhp-day-treatment", package.GetProperty("resource").GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString());
        var issue = Assert.Single(outcome.GetProperty("issue").EnumerateArray());
        Assert.Equal("warning", issue.GetProperty("severity").GetString());
        Assert.Contains(unknown, issue.GetProperty("diagnostics").GetString());
    }

    [Fact]
    public async Task Orders_that_are_not_understood_are_reported_as_warnings()
    {
        var parameters = Parameters(CoverageParameter(),
            $$"""{ "name": "order", "resource": {{Order("H2019", "Coverage/cov-1")}} }""",
            $$"""{ "name": "order", "resource": {{Order("90834", "Coverage/cov-1", id: "order-2")}} }""",
            """{ "name": "order", "resource": { "resourceType": "DeviceRequest", "id": "device-1", "status": "draft", "intent": "order", "codeCodeableConcept": { "text": "x" }, "subject": { "reference": "Patient/synthetic-1" } } }""");

        var body = await PostPackage(parameters);
        var issues = body.GetProperty("parameter").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "operationOutcome")
            .GetProperty("resource").GetProperty("issue").EnumerateArray().Select(i => i.GetProperty("diagnostics").GetString()!).ToList();

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, d => d.Contains("ServiceRequest/order-2"));
        Assert.Contains(issues, d => d.Contains("DeviceRequest"));
    }

    [Fact]
    public async Task Asking_for_a_package_when_nothing_applies_returns_not_found()
    {
        var parameters = Parameters(CoverageParameter(), $$"""{ "name": "order", "resource": {{Order("90834", "Coverage/cov-1")}} }""");

        var response = await _signedIn.PostAsync(Package, Fhir(parameters));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_questionnaire_can_be_read_by_its_id_and_declares_the_DTR_profile()
    {
        var questionnaire = await _signedIn.GetFromJsonAsync<JsonElement>("/fhir/Questionnaire/dtr-mhp-youth");

        Assert.Equal("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire", questionnaire.GetProperty("meta").GetProperty("profile")[0].GetString());
        // Not yet signed off by the plan's clinical lead, so published as a draft.
        Assert.Equal("draft", questionnaire.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(questionnaire.GetProperty("text").GetProperty("div").GetString()));
    }

    [Fact]
    public async Task Questionnaire_errors_reported_by_a_DTR_app_are_accepted()
    {
        var parameters = Parameters(
            $$"""{ "name": "Questionnaire", "valueCanonical": "{{H2019Canonical}}|1" }""",
            """{ "name": "OperationOutcome", "resource": { "resourceType": "OperationOutcome", "issue": [{ "severity": "error", "code": "processing", "diagnostics": "Item asam-level could not be rendered", "expression": ["Questionnaire.item[0]"] }] } }""");

        var response = await _signedIn.PostAsync("/fhir/Questionnaire/$log-questionnaire-errors", Fhir(parameters));
        var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("information", outcome.GetProperty("issue")[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task A_questionnaire_error_report_without_its_OperationOutcome_is_refused()
    {
        var parameters = Parameters($$"""{ "name": "Questionnaire", "valueCanonical": "{{H2019Canonical}}|1" }""");

        var response = await _signedIn.PostAsync("/fhir/Questionnaire/$log-questionnaire-errors", Fhir(parameters));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Value_set_expansion_reports_every_value_set_as_not_found()
    {
        var get = await _signedIn.GetAsync("/fhir/ValueSet/$expand?url=" + Uri.EscapeDataString("http://example.org/ValueSet/x"));
        var post = await _signedIn.PostAsync("/fhir/ValueSet/$expand", Fhir(Parameters("""{ "name": "url", "valueUri": "http://example.org/ValueSet/x" }""")));

        foreach (var response in new[] { get, post })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("OperationOutcome", outcome.GetProperty("resourceType").GetString());
            Assert.Equal("not-found", outcome.GetProperty("issue")[0].GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("GET", "/fhir/Questionnaire/dtr-mhp-youth")]
    [InlineData("POST", "/fhir/Questionnaire/$questionnaire-package")]
    [InlineData("POST", "/fhir/Questionnaire/$log-questionnaire-errors")]
    [InlineData("GET", "/fhir/ValueSet/$expand?url=http://example.org/ValueSet/x")]
    [InlineData("POST", "/fhir/ValueSet/$expand")]
    public async Task Questionnaires_need_a_token(string method, string path)
    {
        var response = await _anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/fhir/Questionnaire/dtr-mhp-youth")]
    [InlineData("POST", "/fhir/Questionnaire/$log-questionnaire-errors")]
    [InlineData("GET", "/fhir/ValueSet/$expand?url=http://example.org/ValueSet/x")]
    public async Task A_token_without_the_questionnaire_scope_cannot_read_questionnaires(string method, string path)
    {
        var submitOnly = await _provider.SignInAsync(factory, scope: "system/Claim.write");

        var response = await submitOnly.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- The answers reach the reviewer ----------

    [Fact]
    public async Task Questionnaire_answers_sent_with_a_request_reach_the_reviewer_as_readable_text()
    {
        var answers = """
            { "resource": { "resourceType": "QuestionnaireResponse", "questionnaire": "http://localhost/fhir/Questionnaire/dtr-mhp-youth|1", "status": "completed",
                "item": [
                  { "linkId": "assessment-date", "answer": [{ "valueDate": "2026-10-30" }] },
                  { "linkId": "placement-risk", "answer": [{ "valueBoolean": true }] },
                  { "linkId": "amount-requested", "answer": [{ "valueString": "10 hours per week" }] }
                ] } }
            """;
        var submitted = await _signedIn.PostAsync("/fhir/Claim/$submit", Fhir(RequestBundle($"REQ-{Guid.NewGuid():N}", answers)));
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString();

        var entry = (await _anonymous.GetFromJsonAsync<JsonElement>("/internal/requests")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var document = Assert.Single(entry.GetProperty("documents").EnumerateArray());
        var text = await _anonymous.GetStringAsync($"/internal/requests/{id}/documents/{document.GetProperty("id").GetString()}");

        Assert.Equal("Questionnaire answers: Therapeutic behavioral services or therapeutic foster care", document.GetProperty("title").GetString());
        Assert.Contains("Date of the most recent assessment: 2026-10-30", text);
        Assert.Contains("Without this service, is the member at risk of a higher level of care, or unable to move to a lower one?: Yes", text);
        Assert.Contains("Amount requested (TBS: hours per week; TFC: placement start date and expected length): 10 hours per week", text);
    }

    // ---------- Helpers ----------

    private const string RevenueCodes = "https://www.nubc.org/CodeSystem/RevenueCodes";

    /// <summary>The coverage information order-sign attaches to one order.</summary>
    private async Task<JsonElement> AnswerFor(string order) =>
        CoverageInfo(Assert.Single((await CallHook(OrderSign, Hook("order-sign", order))).GetProperty("systemActions").EnumerateArray()).GetProperty("resource"));

    private static JsonElement CoverageInfo(JsonElement resource) =>
        resource.GetProperty("extension").EnumerateArray().Single(e => e.GetProperty("url").GetString() == CoverageInformation);

    private static JsonElement Sub(JsonElement extension, string url) =>
        extension.GetProperty("extension").EnumerateArray().Single(e => e.GetProperty("url").GetString() == url);

    private static bool HasSub(JsonElement extension, string url) =>
        extension.GetProperty("extension").EnumerateArray().Any(e => e.GetProperty("url").GetString() == url);

    private static string? Value(JsonElement extension, string url, string valueName) => Sub(extension, url).GetProperty(valueName).GetString();

    private static JsonElement DraftResponse(JsonElement packageParameter) =>
        packageParameter.GetProperty("resource").GetProperty("entry").EnumerateArray()
            .Select(e => e.GetProperty("resource")).Single(r => r.GetProperty("resourceType").GetString() == "QuestionnaireResponse");

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Fhir(string json) => new(json, Encoding.UTF8, "application/fhir+json");

    private static string Parameters(params string[] parameters) =>
        $$"""{ "resourceType": "Parameters", "parameter": [{{string.Join(",", parameters)}}] }""";

    private static string CoverageParameter(string id = "cov-1", string beneficiary = "Patient/synthetic-1") => $$"""
        { "name": "coverage", "resource": { "resourceType": "Coverage", "id": "{{id}}", "status": "active",
          "beneficiary": { "reference": "{{beneficiary}}" }, "payor": [{ "display": "Behavioral Health Plan (synthetic)" }] } }
        """;

    private async Task<JsonElement> PostPackage(string parameters)
    {
        var response = await _signedIn.PostAsync(Package, Fhir(parameters));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Calls a CDS service the way CDS Hooks requires: with a JWT the caller signed for that service's address.</summary>
    private Task<HttpResponseMessage> Post(string path, string body, string? audience = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.Assertion(audience: audience ?? "http://localhost" + path));
        return _anonymous.SendAsync(request);
    }

    private async Task<JsonElement> CallHook(string path, string body)
    {
        var response = await Post(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>A draft ServiceRequest. By default it has what the CRD ServiceRequest profile requires (authoredOn, requester).</summary>
    private static string Order(string code, string? coverage, string id = "order-1", bool conformant = true, string? system = null) => $$"""
        { "resourceType": "ServiceRequest", "id": "{{id}}", "status": "draft", "intent": "order",
          "code": { "coding": [{ "system": "{{system ?? PasFixtures.SystemFor(code)}}", "code": "{{code}}" }] },
          "subject": { "reference": "Patient/synthetic-1" }{{(conformant ? """, "authoredOn": "2026-11-11", "requester": { "reference": "Practitioner/synthetic-1" }""" : "")}}{{(coverage is null ? "" : $$""", "insurance": [{ "reference": "{{coverage}}" }]""")}} }
        """;

    private static string Hook(string hook, string order, string? prefetch = null, string[]? selections = null, string? extension = null) =>
        Hook(hook, [order], prefetch, selections, extension);

    private static string Hook(string hook, string[] orders, string? prefetch = null, string[]? selections = null, string? extension = null) => $$"""
        {
          "hook": "{{hook}}",
          "hookInstance": "{{Guid.NewGuid()}}",
          "context": {
            "userId": "Practitioner/synthetic-1",
            "patientId": "synthetic-1",{{(selections is null ? "" : $"\n    \"selections\": [{string.Join(",", selections.Select(s => $"\"{s}\""))}],")}}
            "draftOrders": { "resourceType": "Bundle", "type": "collection", "entry": [{{string.Join(",", orders.Select(o => $"{{ \"resource\": {o} }}"))}}] }
          }{{(prefetch is null ? "" : $", \"prefetch\": {prefetch}")}}{{(extension is null ? "" : $", \"extension\": {extension}")}}
        }
        """;

    private static string RequestBundle(string requestId, string extraEntry) =>
        PasFixtures.RequestBundle(requestId, extraEntries: extraEntry);
}
