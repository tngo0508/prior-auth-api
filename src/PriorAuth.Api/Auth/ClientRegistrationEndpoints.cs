using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Data;

namespace PriorAuth.Api.Auth;

/// <summary>Internal endpoints plan admins use to register a provider's system. Not part of the FHIR API. The portal's Provider systems page does the same.</summary>
public static class ClientRegistrationEndpoints
{
    public static void MapClientRegistrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/clients").StaffOnly(StaffSignIn.AdminPolicy);

        group.MapGet("", async (PriorAuthDb db) => Results.Json(
            (await db.ProviderClients.OrderBy(c => c.Name).ToListAsync())
            .Select(c => new { c.ClientId, c.Name, c.ContactEmail, c.Active, c.RegisteredAtUtc })));

        // The body is { "name": "...", "contactEmail": "..." (optional), "jwks": { "keys": [ ... ] } }. It is read by hand so that a body of the
        // wrong shape gets a clear 400 or 422 instead of a model-binding exception.
        group.MapPost("", async (HttpRequest http, PriorAuthDb db, TimeProvider clock) =>
        {
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(http.Body); }
            catch (JsonException) { return Results.Problem("The request body is not JSON.", statusCode: StatusCodes.Status400BadRequest); }

            using (document)
            {
                var body = document.RootElement;
                if (body.ValueKind != JsonValueKind.Object) return Refuse("The body must be a JSON object with 'name' and 'jwks'.");
                if (!body.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                    return Refuse("The provider's name is required.");
                if (!body.TryGetProperty("jwks", out var jwks)) return Refuse("jwks is required.");

                var problem = ClientKeys.CheckKeySet(jwks);
                if (problem is not null) return Refuse(problem);

                string? contactEmail = null;
                if (body.TryGetProperty("contactEmail", out var email) && email.ValueKind == JsonValueKind.String)
                {
                    contactEmail = email.GetString()!.Trim();
                    if (contactEmail.Length > 0 && !ProviderClient.LooksLikeEmail(contactEmail)) return Refuse("The contact email is not an email address.");
                }

                var client = ProviderClient.Register(name.GetString()!, jwks.GetRawText(), clock.GetUtcNow().UtcDateTime, contactEmail);
                db.ProviderClients.Add(client);
                await db.SaveChangesAsync();
                return Results.Json(new { client.ClientId, client.Name }, statusCode: StatusCodes.Status201Created);
            }
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
