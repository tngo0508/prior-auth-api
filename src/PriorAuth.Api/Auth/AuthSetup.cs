using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PriorAuth.Api.Data;
using PriorAuth.Api.Fhir;

namespace PriorAuth.Api.Auth;

public static class AuthSetup
{
    /// <summary>The header the reviewer page sends on every internal POST. A cross-site form or link cannot add it.</summary>
    public const string InternalRequestHeader = "X-Requested-By";
    public const string InternalRequestHeaderValue = "prior-auth-reviewer";

    /// <summary>Requires an access token from this server. Each policy also requires one scope.</summary>
    public static IServiceCollection AddProviderSignIn(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<AccessTokenIssuer>();
        services.AddScoped<ClientAssertions>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<AccessTokenIssuer>((options, issuer) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = AccessTokenIssuer.Issuer,
                ValidAudience = AccessTokenIssuer.Audience,
                IssuerSigningKey = issuer.ValidationKey,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            options.Events = new JwtBearerEvents
            {
                // A deactivated client loses access at once, not when its token expires.
                OnTokenValidated = async context =>
                {
                    var clientId = context.Principal?.ClientId();
                    var db = context.HttpContext.RequestServices.GetRequiredService<PriorAuthDb>();
                    var client = string.IsNullOrEmpty(clientId) ? null : await db.ProviderClients.FindAsync(clientId);
                    if (client is not { Active: true }) context.Fail("This client is no longer active.");
                },
                // Errors from the FHIR API are FHIR OperationOutcomes, including sign-in errors.
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await WriteOutcome(context.HttpContext, context.AuthenticateFailure is null
                        ? "An access token is required. Sign in at the token endpoint named in /fhir/.well-known/smart-configuration."
                        : "The access token was refused: it has expired, is not from this server, or its client is no longer active.",
                        StatusCodes.Status401Unauthorized);
                },
                OnForbidden = context => WriteOutcome(context.HttpContext,
                    "The access token does not carry the scope this request needs.", StatusCodes.Status403Forbidden),
            };
        });

        var authorization = services.AddAuthorizationBuilder();
        foreach (var scope in Scopes.All)
            authorization.AddPolicy(scope, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, scope)));

        return services;
    }

    private static Task WriteOutcome(HttpContext http, string message, int status)
    {
        http.Response.StatusCode = status;
        return http.RequestServices.GetRequiredService<FhirJson>().Problem(message, status).ExecuteAsync(http);
    }

    /// <summary>The registered client the access token was issued to.</summary>
    public static string ClientId(this ClaimsPrincipal user) => user.FindFirstValue("client_id") ?? "";

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        (user.FindFirstValue("scope") ?? "").Split(' ').Contains(scope);

    /// <summary>
    /// Interim guard for the internal endpoints until staff sign-in exists (issue #2). They only answer when all of these hold:
    /// the connection comes from this machine; no proxy forwarded it (a proxy on the same machine would make every caller look local);
    /// the Host header names this machine (defeats DNS rebinding from a web page); and, for changes, the reviewer page's own
    /// header is present (defeats a cross-site form). On a shared host (terminal server, Citrix) every signed-in user still counts
    /// as local, so do not run it there.
    /// </summary>
    public static RouteGroupBuilder LocalCallersOnly(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var address = http.Connection.RemoteIpAddress;
            var forwarded = http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("Forwarded")
                            || http.Request.Headers.ContainsKey("X-Forwarded-Host") || http.Request.Headers.ContainsKey("X-Real-IP");
            var host = http.Request.Host.Host;
            var localHost = host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
            var isChange = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method);
            var hasPageHeader = http.Request.Headers[InternalRequestHeader] == InternalRequestHeaderValue;

            return address is not null && IPAddress.IsLoopback(address) && !forwarded && localHost && (!isChange || hasPageHeader)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        return group;
    }
}
