using Common.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthApi;

// Lives in its own database (users_db, see docker-compose.yml) — no schema qualifier needed,
// the database itself is the isolation boundary between services.
public class DatabaseContext(DbContextOptions options) : DbContext(options)
{
    private const string UsersTable = "users";

    internal DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable(UsersTable);
        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<User>()
            .HasIndex(x => x.Email)
            .IsUnique();
    }
}
