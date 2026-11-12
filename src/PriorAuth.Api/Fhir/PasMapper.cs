using System.Security.Cryptography;
using System.Text;
using Hl7.Fhir.Model;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Fhir;

/// <summary>What we need out of a Da Vinci PAS request Bundle.</summary>
/// <param name="Service">What the plan's list says about the requested service, decided with the billing modifiers on the request line.</param>
public record Submission(
    Bundle Bundle, Claim Claim, string ProviderRequestSystem, string ProviderRequestId,
    string MemberName, string? MemberIdentifier, string ServiceSystem, string ServiceCode, string? ServiceDisplay,
    ServiceMatch Service, IReadOnlyList<NewDocument> Documents)
{
    public RequestDetails Details(string clientId, string providerName, string serviceName) => new(
        clientId, ProviderRequestSystem, ProviderRequestId, providerName, MemberName, MemberIdentifier, ServiceSystem, ServiceCode, serviceName);
}

/// <summary>
/// Translates between FHIR (Da Vinci Prior Authorization Support) and the domain model.
/// NOTE: the response shape follows a reading of PAS 2.0.1. Response Bundles were checked with the Firely
/// validator against the PAS 2.0.1 package during the November 2026 audit; there is no validation in the build yet (issue #8).
/// </summary>
public static class PasMapper
{
    public const string ReviewActionUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction";
    public const string ReviewActionCodeUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode";
    public const string ItemPreAuthIssueDateUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-itemPreAuthIssueDate";
    public const string ReviewActionCodeSystem = "https://codesystem.x12.org/005010/306";

    // ---------- Reading a request ----------

    public static (Submission? Submission, string? Error) ReadSubmission(Bundle bundle, ServiceRequirementCatalog catalog)
    {
        if (bundle.Type != Bundle.BundleType.Collection) return (null, "The request Bundle must have type 'collection'.");

        var claims = bundle.Entry.Select(e => e.Resource).OfType<Claim>().ToList();
        if (claims.Count == 0) return (null, "The Bundle must contain a Claim.");
        if (claims.Count > 1) return (null, "The Bundle must contain exactly one Claim.");
        if (bundle.Entry[0].Resource is not Claim) return (null, "The Claim must be the first entry of the Bundle.");
        var claim = claims[0];

        // Modifier elements change the meaning of the request. Ones this service does not understand are refused, not ignored.
        if (claim.Status != FinancialResourceStatusCodes.Active) return (null, "Claim.status must be 'active'.");
        if (claim.Use != ClaimUseCode.Preauthorization) return (null, "Claim.use must be 'preauthorization'.");
        if (claim.ModifierExtension.Count > 0 || claim.Item.Any(i => i.ModifierExtension.Count > 0))
            return (null, "The Claim carries a modifierExtension this service does not support. Updating or cancelling a request is not supported yet.");

        var identifier = claim.Identifier.FirstOrDefault();
        var requestId = identifier?.Value;
        if (string.IsNullOrWhiteSpace(requestId)) return (null, "Claim.identifier must carry the provider's request identifier.");
        if (requestId != requestId.Trim()) return (null, "Claim.identifier.value must not start or end with spaces.");
        if (requestId.Length > PriorAuthDb.MaxIdentifierLength || (identifier!.System?.Length ?? 0) > PriorAuthDb.MaxIdentifierLength)
            return (null, $"Claim.identifier system and value must each be at most {PriorAuthDb.MaxIdentifierLength} characters.");

        // PAS requires the insurer on the request, and the response must echo it.
        if (claim.Insurer is null) return (null, "Claim.insurer must identify the plan the request is addressed to.");

        // One request is one service. Until each line can be reviewed on its own, a Claim with several lines is refused
        // rather than having the later lines silently dropped.
        if (claim.Item.Count == 0) return (null, "Claim.item must name the service requested.");
        if (claim.Item.Count > 1) return (null, "Send one service per request: this plan reviews each service separately, so Claim.item must have exactly one entry.");
        // PAS carries billing modifiers on the line (Claim.item.modifier). A line with none has none, which also decides the match.
        var modifiers = claim.Item[0].Modifier.SelectMany(m => m.Coding).Select(c => c.Code).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var service = catalog.Match(claim.Item[0].ProductOrService?.Coding, modifiers);
        var coding = service.Coding;
        if (string.IsNullOrWhiteSpace(coding?.Code)) return (null, "Claim.item.productOrService must carry a service code.");
        if (string.IsNullOrWhiteSpace(coding.System)) return (null, "Claim.item.productOrService.coding must state its code system (for example HCPCS or CPT).");

        // The member is the Patient the Claim points to, not whichever Patient happens to be first.
        var patient = Resolve<Patient>(bundle, claim.Patient?.Reference);
        if (patient is null) return (null, "Claim.patient must reference a Patient included in the Bundle.");
        var memberName = patient.Name.FirstOrDefault()?.ToString() is { Length: > 0 } name ? name : claim.Patient?.Display ?? "(not stated)";
        var memberId = patient.Identifier.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Value)) is { } id ? $"{id.System}|{id.Value}" : null;

        var (documents, documentError) = ReadDocuments(bundle, catalog);
        if (documentError is not null) return (null, documentError);

        return (new Submission(bundle, claim, identifier.System ?? "", requestId, memberName, memberId, coding.System, coding.Code.Trim(), coding.Display, service, documents), null);
    }

    /// <summary>Finds the resource a reference points to among the Bundle's entries, by fullUrl or by type/id.</summary>
    private static T? Resolve<T>(Bundle bundle, string? reference) where T : Resource
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        foreach (var entry in bundle.Entry)
        {
            if (entry.Resource is not T resource) continue;
            if (entry.FullUrl == reference) return resource;
            if (resource.Id is not null && (reference == $"{resource.TypeName}/{resource.Id}" || (entry.FullUrl?.EndsWith("/" + reference) ?? false))) return resource;
        }
        return null;
    }

    /// <summary>
    /// The Bundle as it is kept on file: document contents are removed because each document is stored once, on its own.
    /// Call after <see cref="ReadSubmission"/>, which reads the contents.
    /// </summary>
    public static Bundle WithoutDocumentContents(Bundle bundle)
    {
        var copy = (Bundle)bundle.DeepCopy();
        foreach (var attachment in AllResources(copy).OfType<DocumentReference>().SelectMany(d => d.Content).Select(c => c.Attachment).Where(a => a?.Data is not null))
            attachment.Data = null; // the size is kept with the stored document; Attachment.size does not round-trip in this SDK version
        return copy;
    }

    /// <summary>Every resource in the Bundle, including those inside a nested Bundle such as a DTR questionnaire response Bundle.</summary>
    private static IEnumerable<Resource> AllResources(Bundle bundle) =>
        bundle.Entry.Select(e => e.Resource).Where(r => r is not null)
            .SelectMany(r => r is Bundle nested ? AllResources(nested) : [r]);

    /// <summary>Supporting paperwork arrives as DocumentReference resources with the file inline.</summary>
    private static (IReadOnlyList<NewDocument> Documents, string? Error) ReadDocuments(Bundle bundle, ServiceRequirementCatalog catalog)
    {
        var documents = new List<NewDocument>();
        var resources = AllResources(bundle).ToList();
        foreach (var reference in resources.OfType<DocumentReference>())
        {
            foreach (var attachment in reference.Content.Select(c => c.Attachment).Where(a => a is not null))
            {
                var title = attachment.Title ?? reference.Description ?? $"Document {documents.Count + 1}";
                if (attachment.Data is null)
                    return ([], $"Document '{title}' must be sent inline in attachment.data. Links are not accepted.");
                if (!SupportingDocument.AllowedContentTypes.Contains(attachment.ContentType, StringComparer.OrdinalIgnoreCase))
                    return ([], $"Document '{title}' has content type '{attachment.ContentType}'. Accepted types: {string.Join(", ", SupportingDocument.AllowedContentTypes)}.");
                if (attachment.Data.Length > SupportingDocument.MaxBytes)
                    return ([], $"Document '{title}' is larger than {SupportingDocument.MaxBytes / (1024 * 1024)} MB.");
                documents.Add(new NewDocument(title, attachment.ContentType!.ToLowerInvariant(), attachment.Data));
            }
        }
        // A completed questionnaire (Da Vinci DTR) is stored as a readable text document for the reviewer.
        foreach (var response in resources.OfType<QuestionnaireResponse>())
        {
            var questionnaireId = (response.Questionnaire ?? "").Split('|')[0].TrimEnd('/').Split('/').Last();
            var questionnaire = catalog.FindQuestionnaire(questionnaireId);
            var lines = new List<string>();
            if (questionnaire is null)
                lines.Add($"Note: this answers questionnaire '{response.Questionnaire}', which is not one of the plan's questionnaires.");
            if (response.Status != QuestionnaireResponse.QuestionnaireResponseStatus.Completed)
                lines.Add($"Note: the provider marked these answers '{response.Status?.ToString().ToLowerInvariant() ?? "no status"}', not completed.");
            var answered = new HashSet<string>();
            var answerLines = new List<string>();
            WriteAnswers(response.Item, questionnaire, answerLines, answered, indent: "");
            if (answerLines.Count == 0 && lines.Count == 0) continue;
            var missing = questionnaire?.Questions.Where(q => q.Required && !answered.Contains(q.LinkId)).Select(q => q.Text).ToList() ?? [];
            if (missing.Count > 0) lines.Add("Required questions not answered: " + string.Join("; ", missing));
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(answerLines);

            var title = $"Questionnaire answers: {questionnaire?.Title ?? questionnaireId}";
            var text = title + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
            documents.Add(new NewDocument(title, "text/plain", Encoding.UTF8.GetBytes(text)));
        }

        return documents.Count > SupportingDocument.MaxPerSubmission
            ? ([], $"At most {SupportingDocument.MaxPerSubmission} documents can be sent at once.")
            : (documents, null);
    }

    private static void WriteAnswers(List<QuestionnaireResponse.ItemComponent> items, QuestionnaireDefinition? questionnaire, List<string> lines, HashSet<string> answered, string indent)
    {
        foreach (var item in items)
        {
            var planQuestion = questionnaire?.Questions.FirstOrDefault(q => q.LinkId == item.LinkId);
            var question = planQuestion?.Text ?? item.Text ?? item.LinkId;
            var answers = item.Answer.Select(a => AnswerText(a.Value, planQuestion)).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (answers.Count > 0 && item.LinkId is not null) answered.Add(item.LinkId);

            if (answers.Count > 0 || item.Item.Count > 0)
                lines.Add($"{indent}{question}: {(answers.Count > 0 ? string.Join("; ", answers) : "")}".TrimEnd());
            WriteAnswers(item.Item, questionnaire, lines, answered, indent + "  ");
            foreach (var answer in item.Answer) WriteAnswers(answer.Item, questionnaire, lines, answered, indent + "  ");
        }
    }

    /// <summary>
    /// An answer as the reviewer reads it. For a choice, the plan's own wording of the option is shown, not the text the
    /// provider's system sent with it; an answer that is not one of the plan's options is marked as such.
    /// </summary>
    private static string? AnswerText(DataType? value, RequirementQuestion? question) => value switch
    {
        null => null,
        FhirBoolean { Value: true } => "Yes",
        FhirBoolean { Value: false } => "No",
        Coding coding when question?.Options.Length > 0 =>
            question.Options.FirstOrDefault(o => RequirementQuestion.OptionCode(o) == coding.Code)
            ?? $"{coding.Display ?? coding.Code} (not one of the plan's options)",
        Coding coding => coding.Display ?? coding.Code,
        Quantity quantity => $"{quantity.Value} {quantity.Unit ?? quantity.Code}".Trim(),
        Attachment attachment => $"[attachment: {attachment.Title ?? attachment.ContentType ?? "untitled"}]",
        ResourceReference reference => reference.Display ?? reference.Reference,
        Date date => date.Value,
        FhirDateTime dateTime => dateTime.Value,
        Time time => time.Value,
        FhirString text => text.Value,
        FhirUri uri => uri.Value,
        Integer integer => integer.Value?.ToString(),
        FhirDecimal number => number.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static Claim? FindClaim(Bundle bundle) => bundle.Entry.Select(e => e.Resource).OfType<Claim>().FirstOrDefault();

    // ---------- Writing an answer ----------

    /// <summary>The answer to $submit or $inquire: the ClaimResponse first, then what it refers to.</summary>
    public static Bundle ToResponseBundle(AuthorizationRequest request, Bundle submitted, string baseUrl, PlanClock clock)
    {
        var claim = FindClaim(submitted)!;
        var response = ToClaimResponse(request, claim, baseUrl, clock, containInformationRequest: false);
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Timestamp = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero),
            Entry = [new Bundle.EntryComponent { FullUrl = $"{baseUrl}/fhir/ClaimResponse/{request.Id}", Resource = response }],
        };

        // References in the ClaimResponse (patient, insurer, requestor) point at entries of the provider's Bundle.
        // Copy those entries across under the same fullUrl so every reference resolves inside the answer.
        foreach (var reference in new[] { response.Patient?.Reference, response.Insurer?.Reference, response.Requestor?.Reference }.Distinct())
        {
            if (string.IsNullOrWhiteSpace(reference)) continue;
            var entry = submitted.Entry.FirstOrDefault(e => e.FullUrl == reference && e.Resource is not null);
            if (entry is not null) bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = entry.FullUrl, Resource = entry.Resource.DeepCopy() as Resource });
        }

        // PAS carries a request for more information as a CommunicationRequest that the ClaimResponse points to.
        if (request.Status == AuthorizationStatus.InformationRequested)
        {
            var fullUrl = $"urn:uuid:{StableGuid(request.Id + "|" + request.Version)}";
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = fullUrl, Resource = InformationRequest(request, claim) });
            response.CommunicationRequest.Add(new ResourceReference(fullUrl));
        }
        return bundle;
    }

    /// <summary>The answer to $inquire when no request matches: a result Bundle with nothing in it.</summary>
    public static Bundle EmptyResultBundle(DateTimeOffset now) => new() { Type = Bundle.BundleType.Collection, Timestamp = now };

    /// <param name="containInformationRequest">
    /// When the ClaimResponse is read on its own there is no Bundle to hold the CommunicationRequest, so it is contained instead.
    /// </param>
    public static ClaimResponse ToClaimResponse(AuthorizationRequest request, Claim claim, string baseUrl, PlanClock clock, bool containInformationRequest = true)
    {
        // X12 306 review action codes, as used by PAS.
        var (outcome, code, display) = request.Status switch
        {
            AuthorizationStatus.Approved when request.Modification is not null => (ClaimProcessingCodes.Complete, "A6", "Modified"),
            AuthorizationStatus.Approved => (ClaimProcessingCodes.Complete, "A1", "Certified in total"),
            AuthorizationStatus.Denied => (ClaimProcessingCodes.Complete, "A3", "Not Certified"),
            AuthorizationStatus.NotRequired => (ClaimProcessingCodes.Complete, "NA", "No Action Required"),
            _ => (ClaimProcessingCodes.Queued, "A4", "Pended"),
        };

        var reviewAction = new Extension { Url = ReviewActionUrl };
        reviewAction.Extension.Add(new Extension(ReviewActionCodeUrl, new CodeableConcept(ReviewActionCodeSystem, code, display)));
        if (request.AuthorizationNumber is not null)
            reviewAction.Extension.Add(new Extension("number", new FhirString(request.AuthorizationNumber)));

        var adjudication = new ClaimResponse.AdjudicationComponent
        {
            Category = new CodeableConcept("http://terminology.hl7.org/CodeSystem/adjudication", "submitted"),
        };
        adjudication.Extension.Add(reviewAction);

        var item = new ClaimResponse.ItemComponent { ItemSequence = claim.Item.FirstOrDefault()?.Sequence ?? 1, Adjudication = [adjudication] };
        if (request.DecidedAtUtc is { } decided && request.Status is AuthorizationStatus.Approved or AuthorizationStatus.Denied)
            item.Extension.Add(new Extension(ItemPreAuthIssueDateUrl, new Date(clock.LocalDate(decided).ToString("yyyy-MM-dd"))));

        var response = new ClaimResponse
        {
            Id = request.Id,
            Meta = new Meta { LastUpdated = new DateTimeOffset(request.LastChangedAtUtc, TimeSpan.Zero), VersionId = request.Version.ToString() },
            Identifier = [new Identifier($"{baseUrl}/fhir/ClaimResponse", request.Id)],
            Status = FinancialResourceStatusCodes.Active,
            Type = claim.Type,
            Use = ClaimUseCode.Preauthorization,
            Patient = claim.Patient,
            // The time this answer took its current form. It does not move when the same answer is read again.
            CreatedElement = new FhirDateTime(new DateTimeOffset(request.LastChangedAtUtc, TimeSpan.Zero)),
            Insurer = claim.Insurer,
            Requestor = claim.Provider,
            Request = new ResourceReference { Identifier = claim.Identifier.FirstOrDefault() },
            Outcome = outcome,
            Disposition = request.Status switch
            {
                AuthorizationStatus.Approved when request.Modification is not null =>
                    $"Approved with changes through {request.AuthorizedThrough:yyyy-MM-dd}: {request.Modification}",
                AuthorizationStatus.Approved => $"Approved through {request.AuthorizedThrough:yyyy-MM-dd}.",
                AuthorizationStatus.Denied => $"Denied: {request.Reason}",
                AuthorizationStatus.InformationRequested => $"More information needed: {request.Reason}",
                AuthorizationStatus.NotRequired => "Prior authorization is not required for this service.",
                _ => "Pended for clinical review.",
            },
            PreAuthRef = request.AuthorizationNumber!,
            PreAuthPeriod = request.AuthorizedThrough is { } through ? new Period { End = through.ToString("yyyy-MM-dd") } : null!,
            Item = [item],
        };

        if (containInformationRequest && request.Status == AuthorizationStatus.InformationRequested)
        {
            var information = InformationRequest(request, claim);
            information.Id = "information-request";
            response.Contained.Add(information);
            response.CommunicationRequest.Add(new ResourceReference("#information-request"));
        }
        return response;
    }

    private static CommunicationRequest InformationRequest(AuthorizationRequest request, Claim claim) => new()
    {
        Status = RequestStatus.Active,
        Subject = claim.Patient,
        Payload = [new CommunicationRequest.PayloadComponent { Content = new FhirString(request.Reason) }],
    };

    /// <summary>The same input always gives the same id, so polling the same state returns the same fullUrl.</summary>
    private static Guid StableGuid(string input) => new(MD5.HashData(Encoding.UTF8.GetBytes(input)));
}
