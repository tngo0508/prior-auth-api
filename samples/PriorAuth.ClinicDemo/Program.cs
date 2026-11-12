// A demo for people who are not developers: a pretend clinic system that asks the plan for approval
// of a service the presenter picks, step by step, in plain words. The presenter can change the form's answers. Synthetic data only. Needs the API running in Development.
//
//   dotnet run --project samples/PriorAuth.ClinicDemo
//
// Then open http://localhost:5030 next to the reviewer page at http://localhost:5020.

using PriorAuth.ClinicDemo;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5030");
var planApi = (builder.Configuration["PlanApi"] ?? "http://localhost:5020").TrimEnd('/');
builder.Services.AddSingleton(new PlanConnection(planApi));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var demo = app.MapGroup("/demo");
demo.MapGet("/setup", () => Results.Ok(new { planApi, reviewerPage = $"{planApi}/" }));
demo.MapGet("/services", (PlanConnection plan) => plan.Run(plan.ServicesAsync));
demo.MapPost("/service", (ServiceChoice choice, PlanConnection plan) => plan.Run(() => plan.SelectAsync(choice.Id)));
demo.MapPost("/check", (PlanConnection plan) => plan.Run(plan.CheckAsync));
demo.MapPost("/form", (PlanConnection plan) => plan.Run(plan.FormAsync));
demo.MapPost("/send", (SendRequest request, PlanConnection plan) => plan.Run(() => plan.SendAsync(request.Answers)));
demo.MapPost("/status", (PlanConnection plan) => plan.Run(plan.StatusAsync));
demo.MapPost("/reset", (PlanConnection plan) => { plan.Reset(); return Results.Ok(new { ok = true }); });

app.Run();

internal sealed record ServiceChoice(string Id);

internal sealed record SendRequest(Dictionary<string, System.Text.Json.JsonElement>? Answers);
