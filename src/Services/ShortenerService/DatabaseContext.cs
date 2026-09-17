using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Constructed via IDbContextFactory<DatabaseContext> (see Program.cs) — this is a worker service,
// not a request-scoped API, so pooled-as-scoped-service (AddDbContextPool) doesn't fit; a factory
// producing one short-lived context per consumed message does.
public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    private const string Schema = "shortener-service";
    private const string LinksTable = "links";

    internal DbSet<Link> Links { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Link>().ToTable(LinksTable);
        modelBuilder.Entity<Link>()
            .HasKey(x => x.Hash);
    }
}
