using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace PriorAuth.Api.Auth;

/// <summary>
/// Which public keys a provider system may register and sign with: RSA keys of at least 2048 bits
/// (for RS384) or EC keys on curve P-384 (for ES384), each with its own kid. Symmetric keys and keys
/// that carry private parts are refused. The same rules are checked again when a key is used, so a
/// key registered before these rules existed cannot be used either.
/// </summary>
public static class ClientKeys
{
    public const int MinimumRsaBits = 2048;
    private const int P384CoordinateBytes = 48;

    /// <summary>JWK members that only a private or symmetric key has (RFC 7518 sections 6.2.2, 6.3.2 and 6.4.1).</summary>
    private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    /// <summary>Whether a key set holds something that must never be shared: private key parts, or a symmetric (shared secret) key.</summary>
    public static bool HoldsSecret(JsonElement jwks) =>
        jwks.ValueKind == JsonValueKind.Object && jwks.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array &&
        keys.EnumerateArray().Any(key => key.ValueKind == JsonValueKind.Object &&
            (PrivateMembers.Any(part => key.TryGetProperty(part, out _)) ||
             (key.TryGetProperty("kty", out var kty) && kty.ValueKind == JsonValueKind.String && kty.GetString() == "oct")));

    /// <summary>Checks a JSON Web Key Set sent at registration. Returns why it is refused, or null if every key is acceptable.</summary>
    public static string? CheckKeySet(JsonElement jwks)
    {
        if (jwks.ValueKind != JsonValueKind.Object || !jwks.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
            return "jwks must be a JSON Web Key Set: an object with a 'keys' array holding at least one key.";

        var kids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var member in keys.EnumerateArray())
        {
            var where = $"jwks.keys[{index++}]";
            if (member.ValueKind != JsonValueKind.Object) return $"{where} must be a JSON object.";
            if (member.TryGetProperty("kty", out var kty) && kty.ValueKind == JsonValueKind.String && kty.GetString() == "oct")
                return $"{where} is a symmetric ('oct') key. Register an RSA or EC public key; a shared secret must never be sent.";
            if (PrivateMembers.Any(part => member.TryGetProperty(part, out _)))
                return $"{where} contains private key material. Register public keys only, and treat that key as compromised.";
            if (!member.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kid.GetString()))
                return $"{where} must have a non-empty string 'kid'.";
            if (!kids.Add(kid.GetString()!)) return $"{where} repeats the kid '{kid.GetString()}'. Each key needs its own kid.";

            JsonWebKey key;
            try { key = new JsonWebKey(member.GetRawText()); }
            catch (Exception) { return $"{where} could not be read as a JSON Web Key."; }

            var problem = Unusable(key);
            if (problem is not null) return $"{where}: {problem}";
        }
        return null;
    }

    /// <summary>Why this key cannot verify a client's signatures, or null if it can.</summary>
    public static string? Unusable(JsonWebKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Kid)) return "the key must have a kid.";
        if (!string.IsNullOrEmpty(key.Use) && key.Use != "sig") return "use must be 'sig' when given.";
        if (key.HasPrivateKey || !string.IsNullOrEmpty(key.K)) return "the key contains private key material.";

        switch (key.Kty)
        {
            case JsonWebAlgorithmsKeyTypes.RSA:
                if (!string.IsNullOrEmpty(key.Alg) && key.Alg != SecurityAlgorithms.RsaSha384) return "an RSA key's alg must be RS384 when given.";
                if (!TryDecode(key.N, out var modulus) || !TryDecode(key.E, out var exponent)) return "an RSA key needs base64url 'n' and 'e'.";
                if (new BigInteger(modulus, isUnsigned: true, isBigEndian: true).GetBitLength() < MinimumRsaBits)
                    return $"RSA keys must be at least {MinimumRsaBits} bits.";
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                }
                catch (CryptographicException) { return "the RSA key is not a valid public key."; }
                return null;

            case JsonWebAlgorithmsKeyTypes.EllipticCurve:
                if (!string.IsNullOrEmpty(key.Alg) && key.Alg != SecurityAlgorithms.EcdsaSha384) return "an EC key's alg must be ES384 when given.";
                if (key.Crv != JsonWebKeyECTypes.P384) return "EC keys must use curve P-384 (for ES384).";
                if (!TryDecode(key.X, out var x) || !TryDecode(key.Y, out var y) || x.Length != P384CoordinateBytes || y.Length != P384CoordinateBytes)
                    return "a P-384 key needs base64url 'x' and 'y' of 48 bytes each.";
                try
                {
                    using var ec = ECDsa.Create();
                    ec.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP384, Q = new ECPoint { X = x, Y = y } });
                }
                catch (CryptographicException) { return "the EC key is not a valid point on P-384."; }
                return null;

            case JsonWebAlgorithmsKeyTypes.Octet:
                return "symmetric ('oct') keys are not accepted.";

            default:
                return "kty must be RSA or EC.";
        }
    }

    /// <summary>Whether a key of this type can check a signature made with this algorithm.</summary>
    public static bool FitsAlgorithm(JsonWebKey key, string? algorithm) => (algorithm, key.Kty) is
        (SecurityAlgorithms.RsaSha384, JsonWebAlgorithmsKeyTypes.RSA) or
        (SecurityAlgorithms.EcdsaSha384, JsonWebAlgorithmsKeyTypes.EllipticCurve);

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)) return false;
        try { bytes = Base64UrlEncoder.DecodeBytes(value); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return false; }
        return bytes.Length > 0;
    }
}
