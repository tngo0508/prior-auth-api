using Hl7.Fhir.Model;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Fhir;

/// <summary>What we need out of a Da Vinci PAS request Bundle.</summary>
public record Submission(Claim Claim, string ProviderRequestId, string ProviderName, string MemberName, string ServiceCode, string? ServiceDisplay, IReadOnlyList<NewDocument> Documents);

/// <summary>
/// Translates between FHIR (Da Vinci Prior Authorization Support) and the domain model.
/// NOTE: the response shape follows a reading of PAS 2.0.1 and has not yet been validated
/// against the published profiles (tracked in GitHub issue #8).
/// </summary>
public static class PasMapper
{
    public const string ReviewActionUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction";
    public const string ReviewActionCodeUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode";
    public const string ReviewActionCodeSystem = "https://codesystem.x12.org/005010/306";

    public static (Submission? Submission, string? Error) ReadSubmission(Bundle bundle)
    {
        var claim = FindClaim(bundle);
        if (claim is null) return (null, "The Bundle must contain a Claim.");
        if (claim.Use != ClaimUseCode.Preauthorization) return (null, "Claim.use must be 'preauthorization'.");

        var requestId = claim.Identifier.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(requestId)) return (null, "Claim.identifier must carry the provider's request identifier.");

        // PAS requires the insurer on the request, and the response must echo it.
        if (claim.Insurer is null) return (null, "Claim.insurer must identify the plan the request is addressed to.");

        var coding = claim.Item.FirstOrDefault()?.ProductOrService?.Coding.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(coding?.Code)) return (null, "Claim.item[0].productOrService must carry a service code.");

        var patient = bundle.Entry.Select(e => e.Resource).OfType<Patient>().FirstOrDefault();
        var memberName = patient?.Name.FirstOrDefault()?.ToString() ?? claim.Patient?.Display ?? "(not stated)";
        var providerName = claim.Provider?.Display ?? claim.Provider?.Reference ?? "(not stated)";

        var (documents, documentError) = ReadDocuments(bundle);
        if (documentError is not null) return (null, documentError);

        return (new Submission(claim, requestId, providerName, memberName, coding.Code, coding.Display, documents), null);
    }

    /// <summary>Supporting paperwork arrives as DocumentReference resources with the file inline.</summary>
    private static (IReadOnlyList<NewDocument> Documents, string? Error) ReadDocuments(Bundle bundle)
    {
        var documents = new List<NewDocument>();
        foreach (var reference in bundle.Entry.Select(e => e.Resource).OfType<DocumentReference>())
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
        return documents.Count > SupportingDocument.MaxPerSubmission
            ? ([], $"At most {SupportingDocument.MaxPerSubmission} documents can be sent at once.")
            : (documents, null);
    }

    public static Claim? FindClaim(Bundle bundle) => bundle.Entry.Select(e => e.Resource).OfType<Claim>().FirstOrDefault();

    public static Bundle ToResponseBundle(AuthorizationRequest request, Claim claim, DateTimeOffset now)
    {
        var response = ToClaimResponse(request, claim, now);
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Timestamp = now,
            Entry = [new Bundle.EntryComponent { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = response }],
        };

        // PAS carries a request for more information as a CommunicationRequest that the ClaimResponse points to.
        if (request.Status == AuthorizationStatus.InformationRequested)
        {
            var fullUrl = $"urn:uuid:{Guid.NewGuid()}";
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = fullUrl,
                Resource = new CommunicationRequest
                {
                    Status = RequestStatus.Active,
                    Subject = claim.Patient,
                    Payload = [new CommunicationRequest.PayloadComponent { Content = new FhirString(request.Reason) }],
                },
            });
            response.CommunicationRequest.Add(new ResourceReference(fullUrl));
        }
        return bundle;
    }

    public static ClaimResponse ToClaimResponse(AuthorizationRequest request, Claim claim, DateTimeOffset now)
    {
        // X12 306 review action codes, as used by PAS.
        var (outcome, code, display) = request.Status switch
        {
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

        return new ClaimResponse
        {
            Id = request.Id,
            Status = FinancialResourceStatusCodes.Active,
            Type = claim.Type,
            Use = ClaimUseCode.Preauthorization,
            Patient = claim.Patient,
            CreatedElement = new FhirDateTime(now),
            Insurer = claim.Insurer,
            Requestor = claim.Provider,
            Request = new ResourceReference { Identifier = claim.Identifier.FirstOrDefault() },
            Outcome = outcome,
            Disposition = request.Status switch
            {
                AuthorizationStatus.Approved => $"Approved through {request.AuthorizedThrough:yyyy-MM-dd}.",
                AuthorizationStatus.Denied => $"Denied: {request.Reason}",
                AuthorizationStatus.InformationRequested => $"More information needed: {request.Reason}",
                AuthorizationStatus.NotRequired => "Prior authorization is not required for this service.",
                _ => "Pended for clinical review.",
            },
            PreAuthRef = request.AuthorizationNumber!,
            PreAuthPeriod = request.AuthorizedThrough is { } through ? new Period { End = through.ToString("yyyy-MM-dd") } : null!,
            Item = [new ClaimResponse.ItemComponent { ItemSequence = 1, Adjudication = [adjudication] }],
        };
    }
}
