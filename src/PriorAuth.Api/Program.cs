using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Fhir;
using PriorAuth.Api.Review;

// NOT READY TO DEPLOY: there is no authentication yet (GitHub issue #6).
// Run it locally with synthetic data only.

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("service-requirements.json", optional: false, reloadOnChange: false);

builder.Services.AddDbContext<PriorAuthDb>((services, options) =>
    options.UseSqlite(services.GetRequiredService<IConfiguration>().GetConnectionString("PriorAuth")));
builder.Services.AddSingleton<FhirJson>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new ServiceRequirementCatalog(
    services.GetRequiredService<IConfiguration>().GetSection("ServiceRequirements").Get<ServiceRequirement[]>() ?? []));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Creates the database on first run. Replace with migrations before the schema needs to change in place.
    scope.ServiceProvider.GetRequiredService<PriorAuthDb>().Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFhirEndpoints();
app.MapReviewerEndpoints();

app.Run();

public partial class Program;
