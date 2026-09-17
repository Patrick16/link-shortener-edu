using Common;
using Common.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace AuthApi;

public class DatabaseContext(IConfiguration configuration) : DbContext
{
    private readonly IConfiguration _configuration = configuration;
    private const string Schema = "auth-service";
    private const string UsersTable = "users";

    internal DbSet<User> Users { get; set; }

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
        modelBuilder.Entity<User>().ToTable(UsersTable);
        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);
    }
}
