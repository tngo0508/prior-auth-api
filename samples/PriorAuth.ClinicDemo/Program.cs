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

try
{
    app.Run();
}
catch (Exception failure) when (PortTaken(failure, out var reserved))
{
    // Another copy of the demo is running, or (on Windows) Hyper-V, WSL or Docker reserved the port.
    Console.Error.WriteLine($"""

        The clinic demo could not start: {failure.Message}
        {(reserved ? "Windows refused the port. It is usually reserved by Hyper-V, WSL or Docker." : "Another program is using the port, often another copy of the demo.")}

        Start it on another port instead (and say where the plan's API is, if that moved too):
          dotnet run --project samples/PriorAuth.ClinicDemo -- --urls http://localhost:5130 --PlanApi {planApi}

        On Windows, list the reserved ports with:
          netsh interface ipv4 show excludedportrange protocol=tcp
        See "If a port is in use" in docs/03-running-and-testing.md.
        """);
    Environment.ExitCode = 1;
}

static bool PortTaken(Exception failure, out bool reserved)
{
    reserved = false;
    var taken = false;
    for (Exception? e = failure; e is not null; e = e.InnerException)
    {
        if (e is Microsoft.AspNetCore.Connections.AddressInUseException) taken = true;
        if (e is System.Net.Sockets.SocketException socket)
        {
            if (socket.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse) taken = true;
            if (socket.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied) taken = reserved = true;
        }
    }
    return taken;
}

internal sealed record ServiceChoice(string Id);

internal sealed record SendRequest(Dictionary<string, System.Text.Json.JsonElement>? Answers);
