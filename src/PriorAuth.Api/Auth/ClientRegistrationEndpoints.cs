using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PriorAuth.Api.Data;

namespace PriorAuth.Api.Auth;

public record RegisterClientBody(string Name, JsonElement Jwks);

/// <summary>Internal endpoints agency staff use to register a provider's system. Not part of the FHIR API.</summary>
public static class ClientRegistrationEndpoints
{
    private static readonly string[] PrivateKeyParts = ["d", "p", "q", "dp", "dq", "qi"];

    public static void MapClientRegistrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/clients").LocalCallersOnly();

        group.MapGet("", async (PriorAuthDb db) => Results.Json(
            (await db.ProviderClients.OrderBy(c => c.Name).ToListAsync())
            .Select(c => new { c.ClientId, c.Name, c.Active, c.RegisteredAtUtc })));

        group.MapPost("", async (RegisterClientBody body, PriorAuthDb db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) return Refuse("The provider's name is required.");
            if (body.Jwks.ValueKind != JsonValueKind.Object || !body.Jwks.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
                return Refuse("jwks must be a JSON Web Key Set with at least one key.");
            if (keys.EnumerateArray().Any(key => PrivateKeyParts.Any(part => key.TryGetProperty(part, out _))))
                return Refuse("Register public keys only. This key set contains private key material; treat that key as compromised.");

            var jwksJson = body.Jwks.GetRawText();
            try { _ = new JsonWebKeySet(jwksJson); }
            catch (ArgumentException) { return Refuse("jwks could not be read as a JSON Web Key Set."); }

            var client = ProviderClient.Register(body.Name, jwksJson, clock.GetUtcNow().UtcDateTime);
            db.ProviderClients.Add(client);
            await db.SaveChangesAsync();
            return Results.Json(new { client.ClientId, client.Name }, statusCode: StatusCodes.Status201Created);
        });

        group.MapPost("{clientId}/deactivate", async (string clientId, PriorAuthDb db) =>
        {
            var client = await db.ProviderClients.FindAsync(clientId);
            if (client is null) return Results.NotFound();
            client.Deactivate();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static IResult Refuse(string message) => Results.Problem(message, statusCode: StatusCodes.Status422UnprocessableEntity);
}
