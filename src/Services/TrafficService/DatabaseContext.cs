using Common;
using Common.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace TrafficService;

public class DatabaseContext(IConfiguration configuration) : DbContext
{
    private readonly IConfiguration _configuration = configuration;
    private const string Schema = "traffic-service";
    private const string ClicksTable = "clicks";

    internal DbSet<Click> Clicks { get; set; }

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
        modelBuilder.Entity<Click>().ToTable(ClicksTable);
        modelBuilder.Entity<Click>()
            .HasKey(x => x.Id);
    }
}
