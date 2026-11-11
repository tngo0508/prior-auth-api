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

    /// <summary>The token endpoint's public address, which a client assertion must name as its audience.</summary>
    public static string Url(HttpRequest http, IConfiguration config) => PublicUrl.Base(http, config) + Path;

    public static void MapTokenEndpoint(this WebApplication app)
    {
        app.MapGet("/fhir/.well-known/smart-configuration", (HttpRequest http, IConfiguration config) => Results.Json(new
        {
            token_endpoint = Url(http, config),
            grant_types_supported = new[] { "client_credentials" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
            token_endpoint_auth_signing_alg_values_supported = ClientAssertions.Algorithms,
            scopes_supported = Scopes.All,
            capabilities = new[] { "client-confidential-asymmetric", "permission-v1" },
        }));

        app.MapPost(Path, async (HttpRequest http, ClientAssertions assertions, AccessTokenIssuer issuer, TimeProvider clock, IConfiguration config) =>
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

            var (client, error) = await assertions.ValidateAsync(form["client_assertion"].ToString(), Url(http, config), subjectMustMatchIssuer: true);
            if (client is null) return Error(401, "invalid_client", error!);

            return Results.Json(new
            {
                access_token = issuer.Issue(client.ClientId, scope, clock.GetUtcNow().UtcDateTime),
                token_type = "bearer",
                expires_in = (int)AccessTokenIssuer.Lifetime.TotalSeconds,
                scope,
            });
        });
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
