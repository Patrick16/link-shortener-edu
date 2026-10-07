using Common.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Constructed via IDbContextFactory<DatabaseContext> (see Program.cs) — this is a worker service,
// not a request-scoped API, so pooled-as-scoped-service (AddDbContextPool) doesn't fit; a factory
// producing one short-lived context per consumed message does.
//
// Lives in its own database (clicks_db, see docker-compose.yml) — no schema qualifier needed, the
// database itself is the isolation boundary between services. Implements IOwnedDbContext (see
// that file) because this service is the one that actually migrates clicks_db.
public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options), IOwnedDbContext
{
    private const string ClicksTable = "clicks";

    internal DbSet<Click> Clicks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Click>().ToTable(ClicksTable);
        // Composite, not just Id: Postgres requires the partition key (ClickedAt) in every
        // unique/primary key on a table declared PARTITION BY RANGE (ClickedAt) - see the
        // ConvertClicksToPartitionedTable migration. Every lookup in this codebase (the dedup check
        // in ClickTrackedConsumer) filters by Id alone via a plain WHERE, not Find/Attach, so this
        // doesn't need any call-site changes - Id leads the composite index, so an Id-only filter
        // still uses it efficiently.
        modelBuilder.Entity<Click>()
            .HasKey(x => new { x.Id, x.ClickedAt });
    }
}
