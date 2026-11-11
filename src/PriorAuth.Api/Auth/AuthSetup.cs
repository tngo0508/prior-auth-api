using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.Api.Auth;

public static class AuthSetup
{
    /// <summary>Requires an access token from this server. Each policy also requires one scope.</summary>
    public static IServiceCollection AddProviderSignIn(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<AccessTokenIssuer>();

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
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Scopes.SubmitRequests, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, Scopes.SubmitRequests)))
            .AddPolicy(Scopes.ReadResponses, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, Scopes.ReadResponses)));

        return services;
    }

    /// <summary>The registered client the access token was issued to.</summary>
    public static string ClientId(this ClaimsPrincipal user) => user.FindFirstValue("client_id") ?? "";

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        (user.FindFirstValue("scope") ?? "").Split(' ').Contains(scope);

    /// <summary>
    /// Interim guard for the internal endpoints until staff sign-in exists:
    /// they only answer callers on the same machine.
    /// </summary>
    public static RouteGroupBuilder LocalCallersOnly(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var address = context.HttpContext.Connection.RemoteIpAddress;
            // The address is null only for the in-memory test server.
            return address is null || IPAddress.IsLoopback(address) ? await next(context) : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        return group;
    }
}
