using Common.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Constructed via IDbContextFactory<DatabaseContext> (see Program.cs) — this is a worker service,
// not a request-scoped API, so pooled-as-scoped-service (AddDbContextPool) doesn't fit; a factory
// producing one short-lived context per consumed message does.
//
// Lives in its own database (links_db, see docker-compose.yml) — no schema qualifier needed, the
// database itself is the isolation boundary between services. Implements IOwnedDbContext (see
// that file) because this service is the one that actually migrates links_db (LinkApi/RedirectApi
// only ever read it, and their own DatabaseContext types deliberately don't implement it).
public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options), IOwnedDbContext
{
    private const string LinksTable = "links";

    internal DbSet<Link> Links { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Link>().ToTable(LinksTable);
        modelBuilder.Entity<Link>()
            .HasKey(x => x.Hash);
        // Backs LinkApi's GET /Links?page=N - filter by UserId (when a token is present) and sort
        // by CreatedAt in the same query.
        modelBuilder.Entity<Link>()
            .HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}
