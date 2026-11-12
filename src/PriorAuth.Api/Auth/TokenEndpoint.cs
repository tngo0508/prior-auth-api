namespace PriorAuth.Api.Auth;

/// <summary>
/// SMART Backend Services sign-in for provider systems.
/// The client sends a short-lived JWT signed with its private key (a "client assertion").
/// If the signature matches a public key registered for that client, it gets an access token.
/// </summary>
public static class TokenEndpoint
{
    public const string Path = "/auth/token";
    public const string AuthorizePath = "/auth/authorize";
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The token endpoint's public address, which a client assertion must name as its audience.</summary>
    public static string Url(HttpRequest http, IConfiguration config) => PublicUrl.Base(http, config) + Path;

    public static void MapTokenEndpoint(this WebApplication app)
    {
        app.MapGet("/fhir/.well-known/smart-configuration", (HttpRequest http, IConfiguration config) => Results.Json(new
        {
            // SMART App Launch STU2 conformance lists authorization_endpoint as REQUIRED, with no exception for a
            // server that only offers Backend Services. This server has no user-facing authorization (no
            // authorization_code grant), so it publishes an endpoint that exists only to answer every request with
            // the OAuth error unsupported_response_type. That keeps the document valid for strict checkers while
            // grant_types_supported (client_credentials only) tells clients the truth.
            authorization_endpoint = PublicUrl.Base(http, config) + AuthorizePath,
            token_endpoint = Url(http, config),
            grant_types_supported = new[] { "client_credentials" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
            token_endpoint_auth_signing_alg_values_supported = ClientAssertions.Algorithms,
            scopes_supported = Scopes.Supported,
            // REQUIRED by SMART; S256 is the only allowed value. Unused here because there is no authorization_code grant.
            code_challenge_methods_supported = new[] { "S256" },
            capabilities = new[] { "client-confidential-asymmetric", "permission-v1", "permission-v2" },
        }));

        app.MapGet(AuthorizePath, () => Error(400, "unsupported_response_type",
            "This server supports only SMART Backend Services (grant_type client_credentials at the token endpoint). There is no user authorization."));

        app.MapPost(Path, async (HttpRequest http, ClientAssertions assertions, AccessTokenIssuer issuer, TimeProvider clock, IConfiguration config) =>
        {
            // RFC 6749 section 5.1: responses that carry (or refuse) tokens must not be cached.
            http.HttpContext.Response.Headers.CacheControl = "no-store";
            http.HttpContext.Response.Headers.Pragma = "no-cache";

            if (!http.HasFormContentType) return Error(400, "invalid_request", "The request must be form encoded.");
            var form = await http.ReadFormAsync();

            if (form["grant_type"] != "client_credentials")
                return Error(400, "unsupported_grant_type", "grant_type must be client_credentials.");
            if (form["client_assertion_type"] != AssertionType)
                return Error(400, "invalid_request", $"client_assertion_type must be {AssertionType}.");

            // The response echoes the spellings the client asked for (v1 or v2) that this server recognises.
            // The access token carries the internal permission names, which the authorization policies check.
            var granted = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(requested => (Requested: requested, Permission: Scopes.ToPermission(requested)))
                .Where(s => s.Permission is not null)
                .DistinctBy(s => s.Requested)
                .ToList();
            if (granted.Count == 0)
                return Error(400, "invalid_scope", $"Request at least one of: {string.Join(' ', Scopes.Supported)}.");

            var (client, error) = await assertions.ValidateAsync(form["client_assertion"].ToString(), Url(http, config), subjectMustMatchIssuer: true);
            if (client is null) return Error(401, "invalid_client", error!);

            var permissions = string.Join(' ', granted.Select(s => s.Permission!).Distinct());
            return Results.Json(new
            {
                access_token = issuer.Issue(client.ClientId, permissions, clock.GetUtcNow().UtcDateTime),
                token_type = "bearer",
                expires_in = (int)AccessTokenIssuer.Lifetime.TotalSeconds,
                scope = string.Join(' ', granted.Select(s => s.Requested)),
            });
        });
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
