using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace RedirectApi;

public class DatabaseContext(DbContextOptions options) : DbContext(options)
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
