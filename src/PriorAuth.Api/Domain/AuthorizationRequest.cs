using System.Security.Cryptography;

namespace PriorAuth.Api.Domain;

public enum AuthorizationStatus
{
    /// <summary>Received and waiting for a reviewer.</summary>
    Pended,
    /// <summary>A reviewer asked the provider for something that is missing. Still undecided.</summary>
    InformationRequested,
    Approved,
    Denied,
    /// <summary>The service is not on the requirements list, so no decision is needed.</summary>
    NotRequired,
}

/// <summary>Thrown when an action would break a rule of the authorization process.</summary>
public class DomainRuleException(string message) : Exception(message);

/// <summary>What a provider asked for, as read from its request. Used to create a request and to check a resend against it.</summary>
public record RequestDetails(
    string ClientId, string ProviderRequestSystem, string ProviderRequestId, string ProviderName,
    string MemberName, string? MemberIdentifier, string ServiceSystem, string ServiceCode, string ServiceName);

/// <summary>
/// A provider's request for approval of one service for one member.
/// All state changes go through the methods here so the rules cannot be bypassed.
/// </summary>
public class AuthorizationRequest
{
    private readonly List<HistoryEntry> _history = [];
    private readonly List<SupportingDocument> _documents = [];
    private readonly List<ReceivedBundle> _resubmissions = [];

    private AuthorizationRequest() { } // for Entity Framework

    public string Id { get; private set; } = "";
    /// <summary>The registered provider system that sent the request. Only it may see the answer.</summary>
    public string ClientId { get; private set; } = "";
    /// <summary>The system of the identifier the provider put on its request (FHIR Claim.identifier.system); empty when none was sent.</summary>
    public string ProviderRequestSystem { get; private set; } = "";
    /// <summary>The identifier the provider put on its request (FHIR Claim.identifier.value).</summary>
    public string ProviderRequestId { get; private set; } = "";
    public string ProviderName { get; private set; } = "";
    public string MemberName { get; private set; } = "";
    /// <summary>The member's identifier as the provider sent it, "system|value", when one was sent.</summary>
    public string? MemberIdentifier { get; private set; }
    public string ServiceSystem { get; private set; } = "";
    public string ServiceCode { get; private set; } = "";
    public string ServiceName { get; private set; } = "";
    /// <summary>The FHIR Bundle as the provider first sent it, with document contents removed (they are kept once, in <see cref="Documents"/>).</summary>
    public string SubmittedBundleJson { get; private set; } = "";
    public DateTime ReceivedAtUtc { get; private set; }
    public AuthorizationStatus Status { get; private set; }
    public string? AuthorizationNumber { get; private set; }
    public DateOnly? AuthorizedThrough { get; private set; }
    /// <summary>The denial reason, or what information is missing.</summary>
    public string? Reason { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecidedBy { get; private set; }
    /// <summary>When anything about the request last changed. Also the answer's "last updated" time.</summary>
    public DateTime LastChangedAtUtc { get; private set; }
    /// <summary>Goes up on every change. The database refuses a save based on an out-of-date copy, so two people cannot both decide.</summary>
    public int Version { get; private set; }
    public IReadOnlyCollection<HistoryEntry> History => _history;
    /// <summary>The paperwork the provider sent with the request or added later.</summary>
    public IReadOnlyCollection<SupportingDocument> Documents => _documents;
    /// <summary>Every later Bundle the provider sent for this request, kept for the record.</summary>
    public IReadOnlyCollection<ReceivedBundle> Resubmissions => _resubmissions;

    public bool IsOpen => Status is AuthorizationStatus.Pended or AuthorizationStatus.InformationRequested;

    public static AuthorizationRequest Receive(
        RequestDetails details, bool authorizationRequired,
        string submittedBundleJson, IReadOnlyCollection<NewDocument> documents, DateTime nowUtc)
    {
        var request = new AuthorizationRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ClientId = details.ClientId,
            ProviderRequestSystem = details.ProviderRequestSystem,
            ProviderRequestId = details.ProviderRequestId,
            ProviderName = details.ProviderName,
            MemberName = details.MemberName,
            MemberIdentifier = details.MemberIdentifier,
            ServiceSystem = details.ServiceSystem,
            ServiceCode = details.ServiceCode,
            ServiceName = details.ServiceName,
            SubmittedBundleJson = submittedBundleJson,
            ReceivedAtUtc = nowUtc,
            Status = authorizationRequired ? AuthorizationStatus.Pended : AuthorizationStatus.NotRequired,
        };
        request.Record(nowUtc, details.ProviderName, authorizationRequired
            ? "Received. Pended for review."
            : "Received. The service does not require prior authorization.");
        request.Attach(documents, details.ProviderName, nowUtc);
        return request;
    }

    /// <summary>
    /// Why a resend under this request's identifier does not describe the same request, or null when it does.
    /// A provider may not reuse a request number for another member or another service.
    /// </summary>
    public string? MismatchWith(RequestDetails resend)
    {
        if (!ServiceSystem.Equals(resend.ServiceSystem, StringComparison.Ordinal) || !ServiceCode.Equals(resend.ServiceCode, StringComparison.OrdinalIgnoreCase))
            return $"Request {ProviderRequestId} was sent for service {ServiceCode}; it cannot be reused for {resend.ServiceCode}. Use a new identifier.";
        var sameMember = MemberIdentifier is not null && resend.MemberIdentifier is not null
            ? MemberIdentifier.Equals(resend.MemberIdentifier, StringComparison.Ordinal)
            : MemberName.Equals(resend.MemberName, StringComparison.OrdinalIgnoreCase);
        return sameMember ? null : $"Request {ProviderRequestId} was sent for a different member. Use a new identifier.";
    }

    /// <summary>
    /// Keeps a later Bundle for the record, unless an identical one is already on file (a plain retry or a poll).
    /// Its documents are handled separately.
    /// </summary>
    /// <param name="bundleHashesOnFile">SHA-256 of the Bundles already kept for this request.</param>
    public void KeepResubmission(string bundleJson, DateTime nowUtc, IReadOnlySet<string> bundleHashesOnFile)
    {
        if (bundleJson == SubmittedBundleJson) return;
        var resubmission = new ReceivedBundle(bundleJson, nowUtc);
        if (bundleHashesOnFile.Contains(resubmission.Sha256)) return;
        _resubmissions.Add(resubmission);
        Touch(nowUtc);
    }

    /// <summary>
    /// The provider answers an information request by sending the missing paperwork.
    /// Documents already on file are ignored, so a plain retry of an earlier send does not count as an answer.
    /// Returns true when something new arrived and the request went back to the reviewer's queue.
    /// </summary>
    /// <param name="documentHashesOnFile">SHA-256 of the documents already stored for this request.</param>
    public bool SupplyInformation(IReadOnlyCollection<NewDocument> documents, DateTime nowUtc, IReadOnlySet<string> documentHashesOnFile)
    {
        if (Status != AuthorizationStatus.InformationRequested)
            throw new DomainRuleException("Information can only be supplied while the reviewer is waiting for it.");

        var onFile = new HashSet<string>(documentHashesOnFile);
        var fresh = documents.Where(d => onFile.Add(d.Sha256)).ToList();
        if (fresh.Count == 0) return false;

        Attach(fresh, ProviderName, nowUtc);
        Status = AuthorizationStatus.Pended;
        Record(nowUtc, ProviderName, $"Provider supplied more information in answer to: {Reason}. Pended for review.");
        Reason = null;
        return true;
    }

    private void Attach(IReadOnlyCollection<NewDocument> documents, string actor, DateTime nowUtc)
    {
        foreach (var document in documents)
        {
            _documents.Add(new SupportingDocument(document.Title, document.ContentType, document.Data, nowUtc));
            Record(nowUtc, actor, $"Document received: {document.Title} ({document.ContentType}, {document.Data.Length:N0} bytes).");
        }
    }

    /// <param name="today">The plan's local date. An approval may not end before it.</param>
    public void Approve(DateOnly through, string authorizationNumber, string reviewer, DateTime nowUtc, DateOnly today)
    {
        EnsureOpen();
        EnsureReviewer(reviewer);
        if (through < today)
            throw new DomainRuleException("An approval must end on or after today.");

        Status = AuthorizationStatus.Approved;
        AuthorizedThrough = through;
        AuthorizationNumber = authorizationNumber;
        Reason = null;
        Decide(reviewer, nowUtc, $"Approved through {through:yyyy-MM-dd} as {authorizationNumber}.");
    }

    public void Deny(string reason, string reviewer, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureReviewer(reviewer);
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleException("A denial must state a specific reason.");

        Status = AuthorizationStatus.Denied;
        Reason = reason.Trim();
        AuthorizationNumber = null;
        AuthorizedThrough = null;
        Decide(reviewer, nowUtc, $"Denied: {Reason}");
    }

    public void RequestInformation(string missing, string reviewer, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureReviewer(reviewer);
        if (string.IsNullOrWhiteSpace(missing))
            throw new DomainRuleException("An information request must say what is missing.");

        Status = AuthorizationStatus.InformationRequested;
        Reason = missing.Trim();
        Record(nowUtc, reviewer, $"More information requested: {Reason}");
    }

    private void Decide(string reviewer, DateTime nowUtc, string text)
    {
        DecidedBy = reviewer.Trim();
        DecidedAtUtc = nowUtc;
        Record(nowUtc, reviewer, text);
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new DomainRuleException($"This request is already {Status} and cannot be changed.");
    }

    private static void EnsureReviewer(string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) throw new DomainRuleException("The reviewer must be identified.");
    }

    private void Record(DateTime nowUtc, string actor, string text)
    {
        _history.Add(new HistoryEntry(nowUtc, actor.Trim(), text));
        Touch(nowUtc);
    }

    private void Touch(DateTime nowUtc)
    {
        LastChangedAtUtc = nowUtc;
        Version++;
    }
}

/// <summary>One line of the audit trail of an authorization request.</summary>
public class HistoryEntry(DateTime atUtc, string actor, string text)
{
    public int Id { get; private set; }
    public DateTime AtUtc { get; private set; } = atUtc;
    public string Actor { get; private set; } = actor;
    public string Text { get; private set; } = text;
}

/// <summary>A later Bundle the provider sent for an existing request, with document contents removed.</summary>
public class ReceivedBundle(string json, DateTime receivedAtUtc)
{
    public int Id { get; private set; }
    public string Json { get; private set; } = json;
    public string Sha256 { get; private set; } = SupportingDocument.Hash(System.Text.Encoding.UTF8.GetBytes(json));
    public DateTime ReceivedAtUtc { get; private set; } = receivedAtUtc;
}

/// <summary>A document as it arrives from the provider, before it is stored.</summary>
public record NewDocument(string Title, string ContentType, byte[] Data)
{
    public string Sha256 { get; } = SupportingDocument.Hash(Data);
}

/// <summary>A piece of paperwork a provider sent to support an authorization request.</summary>
public class SupportingDocument(string title, string contentType, byte[] data, DateTime receivedAtUtc)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerSubmission = 10;
    /// <summary>Only formats a reviewer can open safely. Anything else is refused at the door.</summary>
    public static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png", "image/tiff", "text/plain"];

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public string Title { get; private set; } = title;
    public string ContentType { get; private set; } = contentType;
    public byte[] Data { get; private set; } = data;
    public int SizeBytes { get; private set; } = data.Length;
    /// <summary>SHA-256 of the contents, used to recognise a document already on file.</summary>
    public string Sha256 { get; private set; } = Hash(data);
    public DateTime ReceivedAtUtc { get; private set; } = receivedAtUtc;
}
