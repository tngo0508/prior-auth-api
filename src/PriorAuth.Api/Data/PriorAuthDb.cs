using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PriorAuth.Api.Auth;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Data;

public class PriorAuthDb(DbContextOptions<PriorAuthDb> options) : DbContext(options)
{
    /// <summary>The longest provider request identifier accepted. Longer ones are refused at the door.</summary>
    public const int MaxIdentifierLength = 200;

    /// <summary>
    /// SQL Server compares text case-insensitively by default, so "REQ-1" and "req-1" would be one request.
    /// Identifiers are exact: compare them byte for byte.
    /// </summary>
    private const string ExactCollation = "Latin1_General_100_BIN2";

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
    public DbSet<HistoryEntry> HistoryEntries => Set<HistoryEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder conventions)
    {
        // Every DateTime is stored in UTC. Mark it as UTC when it is read back, so it is written out with a "Z".
        conventions.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        var sqlServer = Database.IsSqlServer();

        var request = model.Entity<AuthorizationRequest>();
        request.HasKey(r => r.Id);
        Exact(request.Property(r => r.ClientId).HasMaxLength(64));
        Exact(request.Property(r => r.ProviderRequestSystem).HasMaxLength(MaxIdentifierLength));
        Exact(request.Property(r => r.ProviderRequestId).HasMaxLength(MaxIdentifierLength));
        // A provider resending the same request must get the same answer, not a second request.
        // Two different providers may use the same identifier.
        request.HasIndex(r => new { r.ClientId, r.ProviderRequestSystem, r.ProviderRequestId }).IsUnique();
        request.HasIndex(r => new { r.Status, r.ReceivedAtUtc });
        request.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        request.Property(r => r.Version).IsConcurrencyToken();
        request.HasMany(r => r.History).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        request.HasMany(r => r.Documents).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
        request.HasMany(r => r.Resubmissions).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.Resubmissions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var document = model.Entity<SupportingDocument>();
        document.HasKey(d => d.Id);
        document.Property(d => d.Title);
        document.Property(d => d.ContentType);
        document.Property(d => d.Data);
        document.Property(d => d.SizeBytes);
        document.Property(d => d.Sha256).HasMaxLength(64);
        document.Property(d => d.ReceivedAtUtc);

        var history = model.Entity<HistoryEntry>().ToTable("HistoryEntry");
        history.HasKey(h => h.Id);
        history.Property(h => h.AtUtc);
        history.Property(h => h.Actor);
        history.Property(h => h.Text);

        var resubmission = model.Entity<ReceivedBundle>();
        resubmission.HasKey(b => b.Id);
        resubmission.Property(b => b.Json);
        resubmission.Property(b => b.Sha256).HasMaxLength(64);
        resubmission.Property(b => b.ReceivedAtUtc);

        model.Entity<ProviderClient>().HasKey(c => c.ClientId);

        void Exact(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
        {
            if (sqlServer) property.UseCollation(ExactCollation);
        }
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
