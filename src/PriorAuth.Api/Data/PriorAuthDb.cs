using Microsoft.EntityFrameworkCore;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Data;

public class PriorAuthDb(DbContextOptions<PriorAuthDb> options) : DbContext(options)
{
    public DbSet<AuthorizationRequest> AuthorizationRequests => Set<AuthorizationRequest>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var request = model.Entity<AuthorizationRequest>();
        request.HasKey(r => r.Id);
        // A provider resending the same request must get the same answer, not a second request.
        request.HasIndex(r => r.ProviderRequestId).IsUnique();
        request.Property(r => r.Status).HasConversion<string>();
        request.HasMany(r => r.History).WithOne().HasForeignKey("AuthorizationRequestId").IsRequired();
        request.Navigation(r => r.History).UsePropertyAccessMode(PropertyAccessMode.Field);

        var history = model.Entity<HistoryEntry>();
        history.HasKey(h => h.Id);
        history.Property(h => h.AtUtc);
        history.Property(h => h.Actor);
        history.Property(h => h.Text);
    }
}
