using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.Api.Tests;

/// <summary>
/// A provider system with a key of any type or size (RSA or EC P-384), for sign-in tests that TestProvider
/// cannot express: ES384, weak keys, and assertions with a missing or wrong kid, typ, sub or iat.
/// </summary>
public sealed class SigningClient
{
    private readonly RSA? _rsa;
    private readonly ECDsa? _ec;

    private SigningClient(RSA? rsa, ECDsa? ec) { _rsa = rsa; _ec = ec; }

    public static SigningClient Rsa(int bits = 2048) => new(RSA.Create(bits), null);
    public static SigningClient EcP384() => new(null, ECDsa.Create(ECCurve.NamedCurves.nistP384));

    public string KeyId { get; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; private set; } = "";

    public string Algorithm => _rsa is not null ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.EcdsaSha384;

    /// <summary>The public JWK for this key, as a dictionary so a test can add or remove members.</summary>
    public Dictionary<string, string> PublicJwk()
    {
        if (_rsa is not null)
        {
            var p = _rsa.ExportParameters(false);
            return new() { ["kty"] = "RSA", ["kid"] = KeyId, ["use"] = "sig", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) };
        }
        var q = _ec!.ExportParameters(false).Q;
        return new() { ["kty"] = "EC", ["kid"] = KeyId, ["use"] = "sig", ["crv"] = "P-384", ["x"] = Base64UrlEncoder.Encode(q.X), ["y"] = Base64UrlEncoder.Encode(q.Y) };
    }

    public Task<HttpResponseMessage> TryRegisterAsync(HttpClient http, object? jwks = null) =>
        http.PostAsJsonAsync("/internal/clients", new { name = "Signing Clinic (synthetic)", jwks = jwks ?? new { keys = new[] { PublicJwk() } } });

    public async Task<SigningClient> RegisterAsync(HttpClient http, object? jwks = null)
    {
        var response = await TryRegisterAsync(http, jwks);
        response.EnsureSuccessStatusCode();
        ClientId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;
        return this;
    }

    /// <summary>
    /// Builds a signed assertion. By default it is a valid SMART assertion for the test server's token endpoint.
    /// kid: null uses this key's id, "" leaves kid out. typ: null keeps the library's "JWT", "" leaves typ out.
    /// </summary>
    public string Assertion(string audience = TestProvider.TokenUrl, string? kid = null, string? typ = null,
        bool includeSubject = true, DateTime? issuedAt = null, string? jti = null)
    {
        var now = DateTime.UtcNow;
        SecurityKey key = _rsa is not null ? new RsaSecurityKey(_rsa) : new ECDsaSecurityKey(_ec);
        if (kid != "") key.KeyId = kid ?? KeyId;

        var claims = new Dictionary<string, object> { ["jti"] = jti ?? Guid.NewGuid().ToString("N") };
        if (includeSubject) claims["sub"] = ClientId;
        var header = new Dictionary<string, object>();
        if (typ is not null && typ != "") header["typ"] = typ;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = ClientId,
            Audience = audience,
            Claims = claims,
            AdditionalHeaderClaims = header,
            IssuedAt = issuedAt ?? now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(key, Algorithm),
        };
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var token = handler.CreateToken(descriptor);
        return typ == "" ? StripTyp(token, key) : token;
    }

    /// <summary>Re-signs the token with a header that has no typ at all (the library always adds one).</summary>
    private string StripTyp(string token, SecurityKey key)
    {
        var parts = token.Split('.');
        var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlEncoder.Decode(parts[0]))!;
        header.Remove("typ");
        var signingInput = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + parts[1];
        var data = System.Text.Encoding.ASCII.GetBytes(signingInput);
        var signature = _rsa is not null
            ? _rsa.SignData(data, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1)
            : _ec!.SignData(data, HashAlgorithmName.SHA384);
        return signingInput + "." + Base64UrlEncoder.Encode(signature);
    }
}
