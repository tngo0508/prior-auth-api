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
    public bool Active { get; private set; }
    public DateTime RegisteredAtUtc { get; private set; }

    public static ProviderClient Register(string name, string jwksJson, DateTime nowUtc) => new()
    {
        ClientId = Guid.NewGuid().ToString("N"),
        Name = name.Trim(),
        JwksJson = jwksJson,
        Active = true,
        RegisteredAtUtc = nowUtc,
    };

    public void Deactivate() => Active = false;
}
