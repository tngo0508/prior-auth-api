using System.Net;
using Hl7.Fhir.Model;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Dtr;

/// <summary>
/// Turns a service requirement's questions into a FHIR Questionnaire (Da Vinci DTR).
/// NOTE: these questionnaires have no CQL pre-population logic, so the provider's system
/// cannot fill answers from the chart automatically. The DTR profile allows that, but
/// automatic filling is the main benefit of DTR and is not delivered yet.
/// </summary>
public static class QuestionnaireBuilder
{
    public const string Version = "1";
    public const string Profile = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire";

    public static string Url(ServiceRequirement requirement, string baseUrl) => $"{baseUrl}/fhir/Questionnaire/{requirement.QuestionnaireId}";

    public static Questionnaire Build(ServiceRequirement requirement, string baseUrl)
    {
        var answerSystem = $"{baseUrl}/fhir/CodeSystem/prior-auth-answers";
        var title = $"Prior authorization request: {requirement.Name}";

        return new Questionnaire
        {
            Id = requirement.QuestionnaireId,
            Meta = new Meta { Profile = [Profile] },
            Text = new Narrative
            {
                Status = Narrative.NarrativeStatus.Generated,
                Div = $"<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>{WebUtility.HtmlEncode(title)}</p><ol>" +
                      string.Concat(requirement.Questions.Select(q => $"<li>{WebUtility.HtmlEncode(q.Text)}</li>")) + "</ol></div>",
            },
            Url = Url(requirement, baseUrl),
            Version = Version,
            Name = "PriorAuth" + requirement.Code,
            Title = title,
            Status = PublicationStatus.Active,
            SubjectType = [ResourceType.Patient],
            Item = requirement.Questions.Select(q => new Questionnaire.ItemComponent
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

    /// <summary>
    /// The package a provider's system downloads: the Questionnaire first, then a draft response to fill in.
    /// </summary>
    public static Bundle Package(ServiceRequirement requirement, string baseUrl, ResourceReference? subject)
    {
        var questionnaire = Build(requirement, baseUrl);
        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new Bundle.EntryComponent { FullUrl = questionnaire.Url, Resource = questionnaire },
                new Bundle.EntryComponent
                {
                    FullUrl = $"urn:uuid:{Guid.NewGuid()}",
                    Resource = new QuestionnaireResponse
                    {
                        Questionnaire = $"{questionnaire.Url}|{Version}",
                        Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
                        Subject = subject,
                    },
                },
            ],
        };
    }
}
