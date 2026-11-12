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

    /// <summary>The internal permissions. Each is an authorization policy, and these names go in the access token's scope claim.</summary>
    public static readonly string[] All = [SubmitRequests, ReadResponses, ReadQuestionnaires];

    /// <summary>
    /// The scope spellings advertised in smart-configuration: the SMART v1 names, the SMART v2 equivalents
    /// (v1 .read = v2 .rs, v1 .write = v2 .cud), and the lowercase spelling DTR STU2 names (system/questionnaire.rs).
    /// <see cref="ToPermission"/> also accepts other v2 permission strings, such as system/Claim.c.
    /// </summary>
    public static readonly string[] Supported =
    [
        SubmitRequests, "system/Claim.c", "system/Claim.cud",
        ReadResponses, "system/ClaimResponse.rs",
        ReadQuestionnaires, "system/Questionnaire.rs", "system/questionnaire.rs",
    ];

    /// <summary>
    /// Maps a requested scope to the internal permission it grants, or null if this server does not offer it.
    /// v1: system/Claim.write, system/ClaimResponse.read, system/Questionnaire.read.
    /// v2: system/{Type}.{permissions}, where the permissions are a non-empty subset of c r u d s in that order:
    /// Claim needs c (submit; $inquire comes with it), ClaimResponse and Questionnaire need r.
    /// The resource type is matched ignoring case because DTR spells it system/questionnaire.rs.
    /// Scopes with parameters (?...) and wildcards are not offered: this server cannot narrow access that way.
    /// </summary>
    public static string? ToPermission(string requested)
    {
        if (!requested.StartsWith("system/", StringComparison.Ordinal) || requested.Contains('?')) return null;
        var dot = requested.LastIndexOf('.');
        if (dot <= "system/".Length || dot == requested.Length - 1) return null;
        var type = requested["system/".Length..dot];
        var access = requested[(dot + 1)..];

        (string Permission, string? V1, char V2Needs)? resource = type.ToLowerInvariant() switch
        {
            "claim" => (SubmitRequests, "write", 'c'),
            "claimresponse" => (ReadResponses, "read", 'r'),
            "questionnaire" => (ReadQuestionnaires, "read", 'r'),
            _ => null,
        };
        if (resource is not { } r) return null;

        // The v1 names are kept exactly as they have always been spelled.
        if (access == r.V1) return requested == r.Permission ? r.Permission : null;
        return IsV2Permissions(access) && access.Contains(r.V2Needs) ? r.Permission : null;
    }

    /// <summary>A non-empty subset of "cruds", in that order, with no repeats.</summary>
    private static bool IsV2Permissions(string access)
    {
        var position = 0;
        foreach (var letter in access)
        {
            var found = "cruds".IndexOf(letter, position);
            if (found < 0) return false;
            position = found + 1;
        }
        return access.Length > 0;
    }
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
