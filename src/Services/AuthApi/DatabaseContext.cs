using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthApi;

public class DatabaseContext(DbContextOptions options) : DbContext(options)
{
    private const string Schema = "auth-service";
    private const string UsersTable = "users";

    internal DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<User>().ToTable(UsersTable);
        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<User>()
            .HasIndex(x => x.Email)
            .IsUnique();
    }
}
