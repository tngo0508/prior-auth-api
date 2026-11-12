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

    /// <summary>
    /// The tables and columns the model needs that a SQLite file lacks, as "Table" or "Table.Column".
    /// A local file is created once and never upgraded, so one made by an older build is missing what was added since.
    /// </summary>
    public IReadOnlyList<string> MissingFromSqliteFile()
    {
        var connection = Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) connection.Open();
        try
        {
            var missing = new List<string>();
            foreach (var table in Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM pragma_table_info($table)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$table";
                parameter.Value = table;
                command.Parameters.Add(parameter);
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) columns.Add(reader.GetString(0));
                if (columns.Count == 0) { missing.Add(table); continue; }

                var tableId = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table);
                missing.AddRange(Model.GetEntityTypes().Where(e => e.GetTableName() == table)
                    .SelectMany(e => e.GetProperties()).Select(p => p.GetColumnName(tableId)).OfType<string>()
                    .Distinct().Where(c => !columns.Contains(c)).Select(c => $"{table}.{c}"));
            }
            return missing;
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    /// <summary>Longest sign-in account kept (DOMAIN\\user or user@domain).</summary>
    public const int MaxAccountLength = 256;

    public DbSet<AuthorizationRequest> AuthorizationRequests => Set<AuthorizationRequest>();
    public DbSet<ProviderClient> ProviderClients => Set<ProviderClient>();
    public DbSet<SupportingDocument> SupportingDocuments => Set<SupportingDocument>();
    public DbSet<HistoryEntry> HistoryEntries => Set<HistoryEntry>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

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
        request.Property(r => r.ReviewType).HasConversion<string>().HasMaxLength(32);
        request.Property(r => r.Version).IsConcurrencyToken();
        request.Property(r => r.DecidedByAccount).HasMaxLength(MaxAccountLength);
        request.Property(r => r.AssignedTo).HasMaxLength(MaxAccountLength);
        request.Property(r => r.AssignedToName).HasMaxLength(256);
        request.Property(r => r.DenialReasonCode).HasMaxLength(64);
        request.HasIndex(r => r.AssignedTo);
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
        model.Entity<ProviderClient>().Property(c => c.ContactEmail).HasMaxLength(320);

        var staff = model.Entity<StaffMember>();
        staff.HasKey(m => m.Account);
        staff.Property(m => m.Account).HasMaxLength(MaxAccountLength);
        staff.Property(m => m.DisplayName).HasMaxLength(256);
        staff.Property(m => m.Roles).HasMaxLength(256);

        void Exact(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
        {
            if (sqlServer) property.UseCollation(ExactCollation);
        }
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
