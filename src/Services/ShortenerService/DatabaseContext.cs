using Common;
using Common.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace ShortenerService;

public class DatabaseContext(IConfiguration configuration) : DbContext
{
    private readonly IConfiguration _configuration = configuration;
    private const string Schema = "shortener-service";
    private const string LinksTable = "links";

    internal DbSet<Link> Links { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(_configuration.GetConnectionString(Constants.PostgresConnectionString), delegate (NpgsqlDbContextOptionsBuilder options)
        {
            options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Link>().ToTable(LinksTable);
        modelBuilder.Entity<Link>()
            .HasKey(x => x.Hash);
    }
}
