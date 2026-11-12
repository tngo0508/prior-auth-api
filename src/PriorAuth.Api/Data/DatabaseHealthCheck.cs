using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PriorAuth.Api.Data;

/// <summary>Healthy when the database answers. Exposed at /health for the hosting platform's monitoring.</summary>
public class DatabaseHealthCheck(PriorAuthDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database cannot be reached.");
}
