using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace RedirectApi;

// Read-only from this service's side (ShortenerService owns writes/migrations) — see its
// DatabaseContext. Points at the same links_db database, no schema qualifier needed.
public class DatabaseContext(DbContextOptions options) : DbContext(options)
{
    private const string LinksTable = "links";

    internal DbSet<Link> Links { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Link>().ToTable(LinksTable);
        modelBuilder.Entity<Link>()
            .HasKey(x => x.Hash);
    }
}
