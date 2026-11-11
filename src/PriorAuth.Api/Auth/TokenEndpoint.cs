using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PriorAuth.Api.Data;

namespace PriorAuth.Api.Auth;

/// <summary>
/// SMART Backend Services sign-in for provider systems.
/// The client sends a short-lived JWT signed with its private key (a "client assertion").
/// If the signature matches a public key registered for that client, it gets an access token.
/// </summary>
public static class TokenEndpoint
{
    public const string Path = "/auth/token";
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    public static readonly string[] AssertionAlgorithms = [SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384];

    private static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>The token endpoint's public address, which a client assertion must name as its audience.</summary>
    public static string Url(HttpRequest http, IConfiguration config) =>
        (config["Auth:PublicBaseUrl"]?.TrimEnd('/') ?? $"{http.Scheme}://{http.Host}") + Path;

    public static void MapTokenEndpoint(this WebApplication app)
    {
        app.MapGet("/fhir/.well-known/smart-configuration", (HttpRequest http, IConfiguration config) => Results.Json(new
        {
            token_endpoint = Url(http, config),
            grant_types_supported = new[] { "client_credentials" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
            token_endpoint_auth_signing_alg_values_supported = AssertionAlgorithms,
            scopes_supported = Scopes.All,
            capabilities = new[] { "client-confidential-asymmetric", "permission-v1" },
        }));

        app.MapPost(Path, async (HttpRequest http, PriorAuthDb db, AccessTokenIssuer issuer, IMemoryCache usedAssertions, TimeProvider clock, IConfiguration config) =>
        {
            if (!http.HasFormContentType) return Error(400, "invalid_request", "The request must be form encoded.");
            var form = await http.ReadFormAsync();

            if (form["grant_type"] != "client_credentials")
                return Error(400, "unsupported_grant_type", "grant_type must be client_credentials.");
            if (form["client_assertion_type"] != AssertionType)
                return Error(400, "invalid_request", $"client_assertion_type must be {AssertionType}.");

            var scope = string.Join(' ', form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Intersect(Scopes.All));
            if (scope.Length == 0)
                return Error(400, "invalid_scope", $"Request at least one of: {string.Join(' ', Scopes.All)}.");

            var assertion = form["client_assertion"].ToString();
            var handler = new JsonWebTokenHandler();
            if (!handler.CanReadToken(assertion)) return InvalidClient("client_assertion is not a JWT.");

            // Read the claimed identity first, only to find which public keys to check the signature against.
            var claimed = handler.ReadJsonWebToken(assertion);
            if (string.IsNullOrEmpty(claimed.Issuer) || claimed.Subject != claimed.Issuer)
                return InvalidClient("iss and sub must both be the client id.");

            var client = await db.ProviderClients.FindAsync(claimed.Issuer);
            if (client is not { Active: true }) return InvalidClient("The client is not registered or is not active.");

            var result = await handler.ValidateTokenAsync(assertion, new TokenValidationParameters
            {
                ValidIssuer = client.ClientId,
                ValidAudience = Url(http, config),
                IssuerSigningKeys = new JsonWebKeySet(client.JwksJson).Keys,
                ValidAlgorithms = AssertionAlgorithms,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = ClockSkew,
            });
            if (!result.IsValid) return InvalidClient("The assertion's signature, audience or expiry is not valid.");

            var now = clock.GetUtcNow().UtcDateTime;
            var jwt = (JsonWebToken)result.SecurityToken;
            if (jwt.ValidTo > now + MaxAssertionLifetime + ClockSkew)
                return InvalidClient("The assertion must expire within five minutes.");
            if (string.IsNullOrEmpty(jwt.Id)) return InvalidClient("The assertion must carry a jti.");

            // Each assertion may be used once. NOTE: this memory is per server instance.
            var replayKey = $"assertion:{client.ClientId}:{jwt.Id}";
            if (usedAssertions.TryGetValue(replayKey, out _)) return InvalidClient("This assertion has already been used.");
            usedAssertions.Set(replayKey, true, new DateTimeOffset(jwt.ValidTo) + ClockSkew);

            return Results.Json(new
            {
                access_token = issuer.Issue(client.ClientId, scope, now),
                token_type = "bearer",
                expires_in = (int)AccessTokenIssuer.Lifetime.TotalSeconds,
                scope,
            });
        });
    }

    private static IResult InvalidClient(string description) => Error(401, "invalid_client", description);

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
