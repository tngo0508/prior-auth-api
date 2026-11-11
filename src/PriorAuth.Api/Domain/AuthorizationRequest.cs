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

/// <summary>
/// A provider's request for approval of one service for one member.
/// All state changes go through the methods here so the rules cannot be bypassed.
/// </summary>
public class AuthorizationRequest
{
    private readonly List<HistoryEntry> _history = [];
    private readonly List<SupportingDocument> _documents = [];

    private AuthorizationRequest() { } // for Entity Framework

    public string Id { get; private set; } = "";
    /// <summary>The registered provider system that sent the request. Only it may see the answer.</summary>
    public string ClientId { get; private set; } = "";
    /// <summary>The identifier the provider put on its request (FHIR Claim.identifier).</summary>
    public string ProviderRequestId { get; private set; } = "";
    public string ProviderName { get; private set; } = "";
    public string MemberName { get; private set; } = "";
    public string ServiceCode { get; private set; } = "";
    public string ServiceName { get; private set; } = "";
    /// <summary>The FHIR Bundle exactly as the provider sent it.</summary>
    public string SubmittedBundleJson { get; private set; } = "";
    public DateTime ReceivedAtUtc { get; private set; }
    public AuthorizationStatus Status { get; private set; }
    public string? AuthorizationNumber { get; private set; }
    public DateOnly? AuthorizedThrough { get; private set; }
    /// <summary>The denial reason, or what information is missing.</summary>
    public string? Reason { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecidedBy { get; private set; }
    public IReadOnlyCollection<HistoryEntry> History => _history;
    /// <summary>The paperwork the provider sent with the request or added later.</summary>
    public IReadOnlyCollection<SupportingDocument> Documents => _documents;

    public bool IsOpen => Status is AuthorizationStatus.Pended or AuthorizationStatus.InformationRequested;

    public static AuthorizationRequest Receive(
        string clientId, string providerRequestId, string providerName, string memberName,
        string serviceCode, string serviceName, bool authorizationRequired,
        string submittedBundleJson, IReadOnlyCollection<NewDocument> documents, DateTime nowUtc)
    {
        var request = new AuthorizationRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            ProviderRequestId = providerRequestId,
            ProviderName = providerName,
            MemberName = memberName,
            ServiceCode = serviceCode,
            ServiceName = serviceName,
            SubmittedBundleJson = submittedBundleJson,
            ReceivedAtUtc = nowUtc,
            Status = authorizationRequired ? AuthorizationStatus.Pended : AuthorizationStatus.NotRequired,
        };
        request.Record(nowUtc, providerName, authorizationRequired
            ? "Received. Pended for review."
            : "Received. The service does not require prior authorization.");
        request.Attach(documents, providerName, nowUtc);
        return request;
    }

    /// <summary>
    /// The provider answers an information request by sending the missing paperwork.
    /// The request goes back to the reviewer's queue.
    /// </summary>
    public void SupplyInformation(IReadOnlyCollection<NewDocument> documents, DateTime nowUtc)
    {
        if (Status != AuthorizationStatus.InformationRequested)
            throw new DomainRuleException("Information can only be supplied while the reviewer is waiting for it.");
        if (documents.Count == 0)
            throw new DomainRuleException("Supplying information requires at least one document.");

        Attach(documents, ProviderName, nowUtc);
        Status = AuthorizationStatus.Pended;
        Reason = null;
        Record(nowUtc, ProviderName, "Provider supplied more information. Pended for review.");
    }

    private void Attach(IReadOnlyCollection<NewDocument> documents, string actor, DateTime nowUtc)
    {
        foreach (var document in documents)
        {
            _documents.Add(new SupportingDocument(document.Title, document.ContentType, document.Data, nowUtc));
            Record(nowUtc, actor, $"Document received: {document.Title} ({document.ContentType}, {document.Data.Length:N0} bytes).");
        }
    }

    public void Approve(DateOnly through, string authorizationNumber, string reviewer, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureReviewer(reviewer);
        if (through < DateOnly.FromDateTime(nowUtc))
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

    private void Record(DateTime nowUtc, string actor, string text) =>
        _history.Add(new HistoryEntry(nowUtc, actor.Trim(), text));
}

/// <summary>One line of the audit trail of an authorization request.</summary>
public class HistoryEntry(DateTime atUtc, string actor, string text)
{
    public int Id { get; private set; }
    public DateTime AtUtc { get; private set; } = atUtc;
    public string Actor { get; private set; } = actor;
    public string Text { get; private set; } = text;
}

/// <summary>A document as it arrives from the provider, before it is stored.</summary>
public record NewDocument(string Title, string ContentType, byte[] Data);

/// <summary>A piece of paperwork a provider sent to support an authorization request.</summary>
public class SupportingDocument(string title, string contentType, byte[] data, DateTime receivedAtUtc)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerSubmission = 10;
    /// <summary>Only formats a reviewer can open safely. Anything else is refused at the door.</summary>
    public static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png", "image/tiff", "text/plain"];

    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public string Title { get; private set; } = title;
    public string ContentType { get; private set; } = contentType;
    public byte[] Data { get; private set; } = data;
    public int SizeBytes { get; private set; } = data.Length;
    public DateTime ReceivedAtUtc { get; private set; } = receivedAtUtc;
}
