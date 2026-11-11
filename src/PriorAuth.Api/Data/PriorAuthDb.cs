using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Data;

public class PriorAuthDb(DbContextOptions<PriorAuthDb> options) : DbContext(options)
{
    /// <summary>Picks the database from configuration: "Database:Provider" is Sqlite (default) or SqlServer.</summary>
    public static void Configure(DbContextOptionsBuilder options, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("PriorAuth");
        if (string.Equals(config["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase))
            options.UseSqlServer(connectionString);
        else
            options.UseSqlite(connectionString);
    }

    public DbSet<AuthorizationRequest> AuthorizationRequests => Set<AuthorizationRequest>();
    public DbSet<ProviderClient> ProviderClients => Set<ProviderClient>();
    public DbSet<SupportingDocument> SupportingDocuments => Set<SupportingDocument>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var request = model.Entity<AuthorizationRequest>();
        request.HasKey(r => r.Id);
        // A provider resending the same request must get the same answer, not a second request.
        // Two different providers may use the same identifier.
        request.HasIndex(r => new { r.ClientId, r.ProviderRequestId }).IsUnique();
        request.Property(r => r.Status).HasConversion<string>();
        request.HasMany(r => r.History).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        request.HasMany(r => r.Documents).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);

        var document = model.Entity<SupportingDocument>();
        document.HasKey(d => d.Id);
        document.Property(d => d.Title);
        document.Property(d => d.ContentType);
        document.Property(d => d.Data);
        document.Property(d => d.SizeBytes);
        document.Property(d => d.ReceivedAtUtc);

        var history = model.Entity<HistoryEntry>();
        history.HasKey(h => h.Id);
        history.Property(h => h.AtUtc);
        history.Property(h => h.Actor);
        history.Property(h => h.Text);

        model.Entity<ProviderClient>().HasKey(c => c.ClientId);
    }
}
