using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PriorAuth.Api.Data;

/// <summary>
/// Used only by the "dotnet ef" command line tool. Migrations are written for SQL Server,
/// so the tool must build the model with the SQL Server provider. It never connects.
/// </summary>
public class DesignTimeDbFactory : IDesignTimeDbContextFactory<PriorAuthDb>
{
    public PriorAuthDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PriorAuthDb>().UseSqlServer("Server=design-time-only;Database=PriorAuth").Options);
}
