using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Constructed via IDbContextFactory<DatabaseContext> (see Program.cs) — this is a worker service,
// not a request-scoped API, so pooled-as-scoped-service (AddDbContextPool) doesn't fit; a factory
// producing one short-lived context per consumed message does.
//
// Lives in its own database (clicks_db, see docker-compose.yml) — no schema qualifier needed, the
// database itself is the isolation boundary between services.
public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    private const string ClicksTable = "clicks";

    internal DbSet<Click> Clicks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Click>().ToTable(ClicksTable);
        modelBuilder.Entity<Click>()
            .HasKey(x => x.Id);
    }
}
