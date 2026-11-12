namespace PriorAuth.ClinicDemo;

/// <summary>
/// What a clinic's record would fill in for each of the plan's questions, so the presenter starts from a complete form
/// and only changes what they want to show. All synthetic. A question not listed here starts blank.
/// </summary>
public static class SuggestedAnswers
{
    private static string Days(int offset) => DateTime.Now.AddDays(offset).ToString("yyyy-MM-dd");

    // Keyed "questionnaire/linkId" first, then by linkId alone.
    private static readonly Dictionary<string, Func<object>> Answers = new()
    {
        ["assessment-date"] = () => Days(-7),
        ["admission-date"] = () => Days(-1),
        ["start-date"] = () => Days(3),
        ["admission-status"] = () => "voluntary",
        ["diagnosis"] = () => "Synthetic example: major depressive disorder, recurrent, severe.",
        ["medical-necessity"] = () => "Synthetic example: danger to self; outpatient and crisis care tried this week without success.",
        ["days-requested"] = () => "5 days",
        ["discharge-plan"] = () => "Synthetic example: step down to outpatient care with a follow-up within 7 days.",
        ["under-21"] = () => true,
        ["target-behaviours"] = () => "Synthetic example: daily outbursts and running away from school.",
        ["placement-risk"] = () => true,
        ["amount-requested"] = () => "10 hours per week for 12 weeks",
        ["transition-plan"] = () => "Synthetic example: step down to weekly therapy as outbursts fall below twice a week.",
        ["impairment"] = () => "Synthetic example: unable to keep a daily routine or attend work.",
        ["lower-level"] = () => "Synthetic example: weekly outpatient therapy for six months without improvement.",
        ["schedule"] = () => "full-day",
        ["days-per-week"] = () => "5 days",
        ["history"] = () => "Synthetic example: three hospital stays and two crisis episodes in the past year.",
        ["forensic"] = () => false,
        ["why-act"] = () => "Synthetic example: has not been able to stay in outpatient care between hospital stays.",
        ["asam-level"] = () => "3-1",
        ["dtr-dmcods-residential/diagnosis"] = () => "Synthetic example: alcohol use disorder, severe.",
        ["justification"] = () => "Synthetic example: unable to stay abstinent in intensive outpatient care; unstable housing.",
        ["perinatal"] = () => false,
    };

    public static object? For(string questionnaireId, string linkId) =>
        Answers.TryGetValue($"{questionnaireId}/{linkId}", out var specific) ? specific()
        : Answers.TryGetValue(linkId, out var general) ? general()
        : null;
}
