using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.Api.Tests;

/// <summary>A stand-in for a provider's system: it owns a key pair, registers, and signs in.</summary>
public sealed class TestProvider
{
    public const string TokenUrl = "http://localhost/auth/token";
    public const string AllScopes = "system/Claim.write system/ClaimResponse.read";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N");

    public string ClientId { get; private set; } = "";

    public static async Task<TestProvider> RegisterAsync(HttpClient http, string name = "Example Clinic (synthetic)")
    {
        var provider = new TestProvider();
        var response = await http.PostAsJsonAsync("/internal/clients", new { name, jwks = provider.PublicKeySet() });
        response.EnsureSuccessStatusCode();
        provider.ClientId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;
        return provider;
    }

    public object PublicKeySet(bool includePrivatePart = false)
    {
        var parameters = _key.ExportParameters(includePrivatePart);
        var key = new Dictionary<string, string>
        {
            ["kty"] = "RSA", ["kid"] = _keyId, ["use"] = "sig", ["alg"] = "RS384",
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus), ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
        if (includePrivatePart) key["d"] = Base64UrlEncoder.Encode(parameters.D);
        return new { keys = new[] { key } };
    }

    /// <summary>Builds the signed JWT a client sends to prove who it is. The optional arguments let a test break one thing.</summary>
    public string Assertion(string? clientId = null, string audience = TokenUrl, TimeSpan? expiresIn = null, string? jti = null, RSA? signWith = null)
    {
        var expires = DateTime.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(2));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId ?? ClientId,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = clientId ?? ClientId, ["jti"] = jti ?? Guid.NewGuid().ToString("N") },
            IssuedAt = expires.AddMinutes(-2),
            NotBefore = expires.AddMinutes(-2),
            Expires = expires,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signWith ?? _key) { KeyId = _keyId }, SecurityAlgorithms.RsaSha384),
        });
    }

    public static Task<HttpResponseMessage> RequestToken(HttpClient http, string assertion, string scope = AllScopes) =>
        http.PostAsync("/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        }));

    /// <summary>Returns an HTTP client that carries this provider's access token.</summary>
    public async Task<HttpClient> SignInAsync(ApiFactory factory, string scope = AllScopes)
    {
        var http = factory.CreateClient();
        var response = await RequestToken(http, Assertion(), scope);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
