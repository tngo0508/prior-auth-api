using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Crd;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;
using PriorAuth.Api.Dtr;
using PriorAuth.Api.Fhir;
using PriorAuth.Api.Review;

// NOT READY TO DEPLOY. Provider systems and plan staff must sign in, but there is no audit log yet and keys are not
// kept across restarts (phase 4). Use synthetic data only.

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("service-requirements.json", optional: false, reloadOnChange: false);

builder.Services.AddDbContext<PriorAuthDb>((services, options) =>
    PriorAuthDb.Configure(options, services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<FhirJson>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlanClock>();
builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new ServiceRequirementCatalog(
        config.GetSection("ServiceRequirements").Get<ServiceRequirement[]>() ?? [],
        config.GetSection("Questionnaires").Get<QuestionnaireDefinition[]>() ?? []);
});
builder.Services.AddProviderSignIn();
builder.Services.AddStaffSignIn(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<DenialReasons>();
builder.Services.AddScoped<ReviewDesk>();
builder.Services.AddRazorPages(options =>
{
    // Every page needs a signed-in staff member with a role. The pages below narrow it further.
    options.Conventions.AuthorizeFolder("/", StaffSignIn.AnyRolePolicy);
    options.Conventions.AuthorizeFolder("/Requests", StaffSignIn.QueuePolicy);
    options.Conventions.AuthorizeFolder("/Providers", StaffSignIn.AdminPolicy);
    options.Conventions.AllowAnonymousToPage("/NoAccess");
    options.Conventions.AllowAnonymousToPage("/SignIn/DevSignIn");
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

var app = builder.Build();

// Build and check the service list and the plan clock now, so a broken file or time zone stops start-up
// instead of failing every call.
app.Services.GetRequiredService<ServiceRequirementCatalog>();
app.Services.GetRequiredService<PlanClock>();
app.Services.GetRequiredService<DenialReasons>();
// Outside Development the public https address must be configured; it is never taken from the caller's Host header.
PublicUrl.ValidateConfiguration(app.Configuration, app.Environment);

using (var scope = app.Services.CreateScope())
{
    // SQL Server: apply any migrations that have not been applied yet.
    // SQLite (local development and tests): create the schema from the model. It is never upgraded: delete the file after model changes.
    var database = scope.ServiceProvider.GetRequiredService<PriorAuthDb>().Database;
    if (database.IsSqlServer()) database.Migrate(); else database.EnsureCreated();
}

// An unexpected failure is logged in full on the server; the caller gets a FHIR error with no internals in it.
// A request the server refused to read (too large, malformed HTTP) keeps its own status, so a client does not retry it forever.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = failure is BadHttpRequestException bad
        ? (bad.StatusCode, bad.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? $"The request body is too large. The limit is {FhirEndpoints.MaxSubmitBytes / (1024 * 1024)} MB."
            : "The request could not be read.")
        : (StatusCodes.Status500InternalServerError, "The server could not process the request. Try again later.");
    context.Response.StatusCode = status;
    await context.RequestServices.GetRequiredService<FhirJson>().Problem(message, status).ExecuteAsync(context);
}));

// Responses carry member information: never cache them, never let a page frame them, never let a browser guess types.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "DENY";
    headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    // Every answer, the portal pages included, may hold member information or depend on who is signed in.
    headers.CacheControl = "no-store";
    headers.Pragma = "no-cache";
    await next();
});

// The FHIR API speaks FHIR JSON only. Say so plainly instead of answering an XML request with JSON.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/fhir"), fhirApp => fhirApp.Use(async (context, next) =>
{
    var fhir = context.RequestServices.GetRequiredService<FhirJson>();
    var format = context.Request.Query["_format"].ToString();
    var accept = context.Request.Headers.Accept.ToString();
    if ((format.Length > 0 && !FhirMediaTypes.IsJson(format)) || (accept.Length > 0 && !FhirMediaTypes.AcceptsJson(accept)))
    {
        await fhir.Problem("This server returns FHIR JSON only (application/fhir+json).", StatusCodes.Status406NotAcceptable).ExecuteAsync(context);
        return;
    }
    var hasBody = context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0;
    if (HttpMethods.IsPost(context.Request.Method) && hasBody && !FhirMediaTypes.IsJson(context.Request.ContentType ?? ""))
    {
        await fhir.Problem("Send the body as application/fhir+json.", StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(context);
        return;
    }
    await next();
}));

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// An error with no body (unknown address, wrong method) still gets an explanation; on the FHIR API, as an OperationOutcome.
app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    var path = http.Request.Path;
    // Windows sign-in answers a portal page the person has no role for with a bare 403. Show them who to ask instead.
    // (The Development sign-in redirects there itself.)
    if (http.Response.StatusCode == StatusCodes.Status403Forbidden && HttpMethods.IsGet(http.Request.Method)
        && !path.StartsWithSegments("/internal") && !path.StartsWithSegments("/fhir") && !path.StartsWithSegments("/auth") && !path.StartsWithSegments("/cds-services"))
    {
        http.Response.Redirect("/no-access");
        return;
    }
    if (!path.StartsWithSegments("/fhir")) return;
    var status = http.Response.StatusCode;
    var message = status switch
    {
        StatusCodes.Status404NotFound => $"There is no FHIR endpoint at {http.Request.Path}.",
        StatusCodes.Status405MethodNotAllowed => $"{http.Request.Method} is not supported at {http.Request.Path}.",
        _ => $"The request was refused ({status}).",
    };
    await http.RequestServices.GetRequiredService<FhirJson>().Problem(message, status).ExecuteAsync(http);
});

app.MapHealthChecks("/health");
app.MapTokenEndpoint();
app.MapFhirEndpoints();
app.MapCrdEndpoints();
app.MapDtrEndpoints();
app.MapReviewerEndpoints();
app.MapClientRegistrationEndpoints();
app.MapStaffSignIn();
app.MapRazorPages();

app.Run();

public partial class Program;
