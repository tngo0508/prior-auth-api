using System.Globalization;
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
public class ClientAssertions(PriorAuthDb db, IMemoryCache used, TimeProvider clock, ILogger<ClientAssertions> log)
{
    public static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384];

    /// <summary>
    /// The one message for every failure that depends on the client's registration (unknown or inactive client,
    /// no matching key, bad signature, audience or expiry, replay), so a caller cannot tell which client ids exist.
    /// The specific reason is logged on the server.
    /// </summary>
    public const string Refused = "The client assertion was not accepted. Check the client id, kid, signature, audience (aud), expiry (exp) and that the jti is new.";

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <param name="jwt">The signed token.</param>
    /// <param name="audience">The address the token must have been made for.</param>
    /// <param name="subjectMustMatchIssuer">SMART requires sub to equal iss. CDS Hooks has no sub claim.</param>
    public async Task<(ProviderClient? Client, string? Error)> ValidateAsync(string jwt, string audience, bool subjectMustMatchIssuer)
    {
        var handler = new JsonWebTokenHandler();
        var now = clock.GetUtcNow().UtcDateTime;

        // Read the claimed identity and header first, only to find which public key to check the signature against.
        // Nothing read here is trusted until the signature has been checked.
        JsonWebToken claimed;
        try
        {
            if (!handler.CanReadToken(jwt)) return (null, "The token is not a JWT.");
            claimed = handler.ReadJsonWebToken(jwt);
        }
        catch (Exception e) when (e is ArgumentException or SecurityTokenException or FormatException or System.Text.Json.JsonException)
        {
            return (null, "The token is not a JWT.");
        }

        // These checks do not depend on any registration, so they can say exactly what is wrong.
        if (string.IsNullOrEmpty(claimed.Issuer)) return (null, "iss must be the client id.");
        if (subjectMustMatchIssuer && claimed.Subject != claimed.Issuer) return (null, "iss and sub must both be the client id.");
        if (!Algorithms.Contains(claimed.Alg)) return (null, "The token must be signed with RS384 or ES384.");
        if (string.IsNullOrEmpty(claimed.Kid)) return (null, "The token header must carry the kid of the registered key that signed it.");
        if (!string.IsNullOrEmpty(claimed.Typ) && !string.Equals(claimed.Typ, "JWT", StringComparison.OrdinalIgnoreCase))
            return (null, "The token header's typ must be JWT.");
        if (!claimed.TryGetClaim("exp", out _)) return (null, "The token must carry exp.");
        if (claimed.ValidTo > now + MaxLifetime + ClockSkew) return (null, "The token must expire within five minutes.");
        if (string.IsNullOrEmpty(claimed.Id)) return (null, "The token must carry a jti.");
        if (claimed.TryGetClaim("iat", out var iat))
        {
            if (!double.TryParse(iat.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return (null, "iat must be a number of seconds since the epoch.");
            if (seconds > (now + ClockSkew - DateTime.UnixEpoch).TotalSeconds) return (null, "iat must not be in the future.");
        }

        var client = await db.ProviderClients.FindAsync(claimed.Issuer);
        if (client is not { Active: true }) return Refuse(claimed.Issuer, "the client is not registered or is not active");

        // SMART: use the single registered key whose kid matches the header kid and whose type fits alg.
        // No other key is tried.
        var matching = new JsonWebKeySet(client.JwksJson).Keys.Where(k => k.Kid == claimed.Kid).ToList();
        if (matching.Count != 1) return Refuse(client.ClientId, $"{matching.Count} registered keys have kid '{claimed.Kid}'");
        var key = matching[0];
        if (!ClientKeys.FitsAlgorithm(key, claimed.Alg)) return Refuse(client.ClientId, $"key '{key.Kid}' ({key.Kty}) does not fit alg {claimed.Alg}");
        var unusable = ClientKeys.Unusable(key);
        if (unusable is not null) return Refuse(client.ClientId, $"key '{key.Kid}' is not acceptable: {unusable}");

        var result = await handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = client.ClientId,
            ValidAudience = audience,
            IssuerSigningKey = key,
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = Algorithms,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = ClockSkew,
        });
        if (!result.IsValid) return Refuse(client.ClientId, $"validation failed: {result.Exception?.Message}");

        var token = (JsonWebToken)result.SecurityToken;

        // Each token may be used once. Checking and recording happen under one lock so that two requests
        // carrying the same token cannot both pass. NOTE: this memory is per server instance (issue #14).
        var replayKey = $"assertion:{client.ClientId}:{token.Id}";
        lock (used)
        {
            if (used.TryGetValue(replayKey, out _)) return Refuse(client.ClientId, $"jti '{token.Id}' was already used");
            used.Set(replayKey, true, new DateTimeOffset(token.ValidTo, TimeSpan.Zero) + ClockSkew);
        }

        return (client, null);
    }

    private (ProviderClient? Client, string? Error) Refuse(string clientId, string reason)
    {
        log.LogInformation("Client assertion from {ClientId} refused: {Reason}", clientId, reason);
        return (null, Refused);
    }
}

public static class PublicUrl
{
    public const string ConfigKey = "Auth:PublicBaseUrl";

    /// <summary>
    /// The address callers use to reach this server, used as the audience client assertions must name and in
    /// published URLs. Outside Development <see cref="ValidateConfiguration"/> makes sure Auth:PublicBaseUrl is set;
    /// only in Development does this fall back to the request's own scheme and Host header.
    /// </summary>
    public static string Base(HttpRequest http, IConfiguration config) =>
        config[ConfigKey]?.TrimEnd('/') ?? $"{http.Scheme}://{http.Host}";

    /// <summary>
    /// Call at start-up. Outside Development, Auth:PublicBaseUrl must be an absolute https URL; otherwise the
    /// token audience and discovery document would follow whatever Host header a caller sends.
    /// </summary>
    /// <exception cref="InvalidOperationException">The setting is missing or is not an absolute https URL.</exception>
    public static void ValidateConfiguration(IConfiguration config, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;
        var value = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{ConfigKey} must be set outside Development (for example https://pa.example.gov).");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{ConfigKey} must be an absolute https URL; '{value}' is not.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{ConfigKey} must not have a query or fragment; '{value}' does.");
    }
}
