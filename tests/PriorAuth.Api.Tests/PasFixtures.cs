namespace PriorAuth.Api.Tests;

/// <summary>The one Da Vinci PAS request Bundle every test file builds on, so a change to what a valid request is lands in one place.</summary>
public static class PasFixtures
{
    public const string Hcpcs = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    public const string Cpt = "http://www.ama-assn.org/go/cpt";
    public const string PatientFullUrl = "urn:uuid:22222222-2222-2222-2222-222222222222";

    /// <summary>HCPCS codes start with a letter; CPT codes are five digits.</summary>
    public static string SystemFor(string code) => char.IsDigit(code[0]) ? Cpt : Hcpcs;

    /// <summary>
    /// A request for one service for one member. <paramref name="extraEntries"/> are complete Bundle entries
    /// (for example DocumentReference or QuestionnaireResponse) appended after the Claim and Patient.
    /// </summary>
    public static string RequestBundle(
        string requestId, string serviceCode = "H2019", string use = "preauthorization",
        string provider = "Example Clinic (synthetic)", string memberId = "SYN-0001", string family = "Testpatient",
        string? items = null, params string[] extraEntries) => $$"""
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
                "use": "{{use}}",
                "patient": { "reference": "{{PatientFullUrl}}" },
                "created": "2026-11-11",
                "insurer": { "display": "County Behavioral Health Plan (synthetic)" },
                "provider": { "display": "{{provider}}" },
                "priority": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/processpriority", "code": "normal" }] },
                "insurance": [{ "sequence": 1, "focal": true, "coverage": { "display": "Synthetic coverage" } }],
                "item": {{items ?? Item(1, serviceCode)}}
              }
            },
            {
              "fullUrl": "{{PatientFullUrl}}",
              "resource": { "resourceType": "Patient",
                "identifier": [{ "system": "http://example.org/member-id", "value": "{{memberId}}" }],
                "name": [{ "family": "{{family}}", "given": ["Alex"] }] }
            }{{string.Concat(extraEntries.Select(e => "," + e))}}
          ]
        }
        """;

    /// <summary>A Claim.item array holding one service.</summary>
    public static string Item(int sequence, string code) =>
        $$"""[{ "sequence": {{sequence}}, "productOrService": { "coding": [{ "system": "{{SystemFor(code)}}", "code": "{{code}}" }] } }]""";
}
