using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Crd;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Dtr;
using PriorAuth.Api.Fhir;
using PriorAuth.Api.Review;

// NOT READY TO DEPLOY. Provider systems must sign in, but agency staff sign-in does not exist yet:
// the internal endpoints only answer callers on the same machine. Use synthetic data only.

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("service-requirements.json", optional: false, reloadOnChange: false);

builder.Services.AddDbContext<PriorAuthDb>((services, options) =>
    PriorAuthDb.Configure(options, services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<FhirJson>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new ServiceRequirementCatalog(
    services.GetRequiredService<IConfiguration>().GetSection("ServiceRequirements").Get<ServiceRequirement[]>() ?? []));
builder.Services.AddProviderSignIn();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // SQL Server: apply any migrations that have not been applied yet.
    // SQLite (local development and tests): create the schema from the model.
    var database = scope.ServiceProvider.GetRequiredService<PriorAuthDb>().Database;
    if (database.IsSqlServer()) database.Migrate(); else database.EnsureCreated();
}

// An unexpected failure is logged in full on the server; the caller gets a FHIR error with no internals in it.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.RequestServices.GetRequiredService<FhirJson>()
        .Problem("The server could not process the request. Try again later.", StatusCodes.Status500InternalServerError)
        .ExecuteAsync(context);
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapTokenEndpoint();
app.MapFhirEndpoints();
app.MapCrdEndpoints();
app.MapDtrEndpoints();
app.MapReviewerEndpoints();
app.MapClientRegistrationEndpoints();

app.Run();

public partial class Program;
