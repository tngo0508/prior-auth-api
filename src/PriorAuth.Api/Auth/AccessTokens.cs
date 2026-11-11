using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.Api.Auth;

public static class Scopes
{
    /// <summary>Allows submitting and inquiring about authorization requests.</summary>
    public const string SubmitRequests = "system/Claim.write";
    /// <summary>Allows reading authorization responses.</summary>
    public const string ReadResponses = "system/ClaimResponse.read";

    /// <summary>Allows downloading the questionnaires that document a request.</summary>
    public const string ReadQuestionnaires = "system/Questionnaire.read";

    public static readonly string[] All = [SubmitRequests, ReadResponses, ReadQuestionnaires];
}

/// <summary>
/// Creates the short-lived access tokens this server hands out, and holds the key that verifies them.
/// NOTE: the key is generated at start-up, so tokens stop working on restart and a second
/// server instance would not accept them. Load a shared key from a secret store before running
/// more than one instance.
/// </summary>
public class AccessTokenIssuer
{
    public const string Issuer = "prior-auth-api";
    public const string Audience = "prior-auth-api";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

    public SecurityKey ValidationKey => _key;

    public string Issue(string clientId, string scope, DateTime nowUtc) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = Audience,
        Claims = new Dictionary<string, object> { ["sub"] = clientId, ["client_id"] = clientId, ["scope"] = scope },
        IssuedAt = nowUtc,
        NotBefore = nowUtc,
        Expires = nowUtc + Lifetime,
        SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
    });
}
