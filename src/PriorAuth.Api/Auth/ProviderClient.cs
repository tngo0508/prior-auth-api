namespace PriorAuth.Api.Auth;

/// <summary>
/// A provider's software system that is registered to call the API.
/// It proves who it is with a private key; only the matching public keys are stored here.
/// </summary>
public class ProviderClient
{
    private ProviderClient() { } // for Entity Framework

    public string ClientId { get; private set; } = "";
    /// <summary>The provider organization's name, as verified at registration.</summary>
    public string Name { get; private set; } = "";
    /// <summary>The client's public keys as a JSON Web Key Set.</summary>
    public string JwksJson { get; private set; } = "";
    /// <summary>Where the plan emails the provider when a request needs its attention. Optional.</summary>
    public string? ContactEmail { get; private set; }
    public bool Active { get; private set; }
    public DateTime RegisteredAtUtc { get; private set; }

    public static ProviderClient Register(string name, string jwksJson, DateTime nowUtc, string? contactEmail = null) => new()
    {
        ClientId = Guid.NewGuid().ToString("N"),
        Name = name.Trim(),
        ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
        JwksJson = jwksJson,
        Active = true,
        RegisteredAtUtc = nowUtc,
    };

    public void Deactivate() => Active = false;

    /// <summary>A light check: one @ with text either side and a dot in the domain. Delivery proves the rest.</summary>
    public static bool LooksLikeEmail(string value) =>
        value.Length <= 320 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
