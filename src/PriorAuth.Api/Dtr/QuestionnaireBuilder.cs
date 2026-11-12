using System.Net;
using Hl7.Fhir.Model;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Dtr;

/// <summary>
/// Turns one of the plan's questionnaires into a FHIR Questionnaire (Da Vinci DTR). Several services may share one.
/// NOTE: these questionnaires have no CQL pre-population logic, so the provider's system
/// cannot fill answers from the chart automatically. The DTR profile allows that, but
/// automatic filling is the main benefit of DTR and is not delivered yet.
/// </summary>
public static class QuestionnaireBuilder
{
    public const string Version = "1";
    public const string Profile = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire";

    public static string Url(string questionnaireId, string baseUrl) => $"{baseUrl}/fhir/Questionnaire/{questionnaireId}";

    public static Questionnaire Build(QuestionnaireDefinition definition, string baseUrl)
    {
        var answerSystem = $"{baseUrl}/fhir/CodeSystem/prior-auth-answers";
        var documents = definition.Documentation.Length == 0
            ? ""
            : "<p>Documents to send with the request:</p><ul>" + string.Concat(definition.Documentation.Select(d => $"<li>{WebUtility.HtmlEncode(d)}</li>")) + "</ul>";
        var draftNote = definition.IsDraft ? "<p>Draft: not yet signed off by the plan's clinical lead.</p>" : "";

        return new Questionnaire
        {
            Id = definition.Id,
            Meta = new Meta { Profile = [Profile] },
            Text = new Narrative
            {
                Status = Narrative.NarrativeStatus.Generated,
                Div = $"<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>{WebUtility.HtmlEncode(definition.Title)}</p>{draftNote}<ol>" +
                      string.Concat(definition.Questions.Select(q => $"<li>{WebUtility.HtmlEncode(q.Text)}</li>")) + $"</ol>{documents}</div>",
            },
            Url = Url(definition.Id, baseUrl),
            Version = Version,
            Name = Name(definition.Id),
            Title = definition.Title,
            // A questionnaire the plan has not signed off is published as a draft, so no one mistakes it for final policy.
            Status = definition.IsDraft ? PublicationStatus.Draft : PublicationStatus.Active,
            Description = definition.Documentation.Length == 0 ? null : "Documents to send with the request: " + string.Join("; ", definition.Documentation) + ".",
            SubjectType = [ResourceType.Patient],
            Item = definition.Questions.Select(q => new Questionnaire.ItemComponent
            {
                LinkId = q.LinkId,
                Text = q.Text,
                Required = q.Required,
                Type = q.Type.ToLowerInvariant() switch
                {
                    "boolean" => Questionnaire.QuestionnaireItemType.Boolean,
                    "date" => Questionnaire.QuestionnaireItemType.Date,
                    "text" => Questionnaire.QuestionnaireItemType.Text,
                    "choice" => Questionnaire.QuestionnaireItemType.Choice,
                    _ => Questionnaire.QuestionnaireItemType.String,
                },
                AnswerOption = q.Options.Select(option => new Questionnaire.AnswerOptionComponent
                {
                    Value = new Coding(answerSystem, RequirementQuestion.OptionCode(option), option),
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>A computer-friendly name (FHIR: starts with a capital letter, letters and digits only), for example DtrMhpYouth.</summary>
    private static string Name(string id) =>
        string.Concat(id.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>
    /// The package a provider's system downloads: the Questionnaire first, then a draft response to fill in.
    /// The draft follows the DTR QuestionnaireResponse profile: it names the patient, when it was made,
    /// the Coverage(s) and order(s) it is for (qr-context), its purpose (intendedUse "withpa") and one item
    /// per question, unanswered. Context or subject the caller did not supply is left out, not invented.
    /// </summary>
    public static Bundle Package(QuestionnaireDefinition definition, string baseUrl, DraftContext context)
    {
        var questionnaire = Build(definition, baseUrl);
        var draft = new QuestionnaireResponse
        {
            Meta = new Meta { Profile = [ResponseProfile] },
            Questionnaire = $"{questionnaire.Url}|{Version}",
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
            Subject = context.Subject,
            Authored = new FhirDateTime(context.Authored).Value,
            Item = definition.Questions.Select(q => new QuestionnaireResponse.ItemComponent { LinkId = q.LinkId, Text = q.Text }).ToList(),
        };
        foreach (var reference in context.Coverages.Concat(context.Orders))
            draft.Extension.Add(new Extension(ContextExtension, new ResourceReference(reference)));
        draft.Extension.Add(new Extension(IntendedUseExtension, new CodeableConcept(DocReasonSystem, "withpa", "Include in prior authorization")));

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new Bundle.EntryComponent { FullUrl = questionnaire.Url, Resource = questionnaire },
                new Bundle.EntryComponent { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = draft },
            ],
        };
    }

    public const string ResponseProfile = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse";
    public const string ContextExtension = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-context";
    public const string IntendedUseExtension = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/intendedUse";
    /// <summary>CRD 2.0.1 DocReason codes (withpa, withclaim, ...) live in the CRD temporary code system.</summary>
    public const string DocReasonSystem = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";
}

/// <summary>What a draft QuestionnaireResponse is for: the patient, and local references to the Coverage(s) and order(s).</summary>
public sealed record DraftContext(ResourceReference? Subject, IReadOnlyList<string> Coverages, IReadOnlyList<string> Orders, DateTimeOffset Authored);
