using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PriorAuth.Api.Data;

namespace PriorAuth.Api.Auth;

/// <summary>
/// Checks a JWT that a registered provider system signed with its private key.
/// Used in two places: signing in at the token endpoint (SMART Backend Services),
/// and calling the CDS Hooks services directly (CDS Hooks sends such a JWT with every call).
/// </summary>
public class ClientAssertions(PriorAuthDb db, IMemoryCache used, TimeProvider clock)
{
    public static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384];

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <param name="jwt">The signed token.</param>
    /// <param name="audience">The address the token must have been made for.</param>
    /// <param name="subjectMustMatchIssuer">SMART requires sub to equal iss. CDS Hooks has no sub claim.</param>
    public async Task<(ProviderClient? Client, string? Error)> ValidateAsync(string jwt, string audience, bool subjectMustMatchIssuer)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(jwt)) return (null, "The token is not a JWT.");

        // Read the claimed identity first, only to find which public keys to check the signature against.
        var claimed = handler.ReadJsonWebToken(jwt);
        if (string.IsNullOrEmpty(claimed.Issuer)) return (null, "iss must be the client id.");
        if (subjectMustMatchIssuer && claimed.Subject != claimed.Issuer) return (null, "iss and sub must both be the client id.");

        var client = await db.ProviderClients.FindAsync(claimed.Issuer);
        if (client is not { Active: true }) return (null, "The client is not registered or is not active.");

        var result = await handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = client.ClientId,
            ValidAudience = audience,
            IssuerSigningKeys = new JsonWebKeySet(client.JwksJson).Keys,
            ValidAlgorithms = Algorithms,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = ClockSkew,
        });
        if (!result.IsValid) return (null, "The token's signature, audience or expiry is not valid.");

        var now = clock.GetUtcNow().UtcDateTime;
        var token = (JsonWebToken)result.SecurityToken;
        if (token.ValidTo > now + MaxLifetime + ClockSkew) return (null, "The token must expire within five minutes.");
        if (string.IsNullOrEmpty(token.Id)) return (null, "The token must carry a jti.");

        // Each token may be used once. NOTE: this memory is per server instance.
        var replayKey = $"assertion:{client.ClientId}:{token.Id}";
        if (used.TryGetValue(replayKey, out _)) return (null, "This token has already been used.");
        used.Set(replayKey, true, new DateTimeOffset(token.ValidTo) + ClockSkew);

        return (client, null);
    }
}

public static class PublicUrl
{
    /// <summary>The address callers use to reach this server. Set Auth:PublicBaseUrl when behind a proxy.</summary>
    public static string Base(HttpRequest http, IConfiguration config) =>
        config["Auth:PublicBaseUrl"]?.TrimEnd('/') ?? $"{http.Scheme}://{http.Host}";
}
