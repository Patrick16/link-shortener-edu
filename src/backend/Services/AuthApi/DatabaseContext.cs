using AuthApi.Models;
using Common.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthApi;

// Lives in its own database (users_db, see docker-compose.yml) — no schema qualifier needed,
// the database itself is the isolation boundary between services. Implements IOwnedDbContext
// (see that file) because this service is the one that actually migrates users_db.
public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options), IOwnedDbContext
{
    private const string UsersTable = "users";
    private const string RefreshTokensTable = "refresh_tokens";

    internal DbSet<User> Users { get; set; }

    internal DbSet<RefreshToken> RefreshTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable(UsersTable);
        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<User>()
            .HasIndex(x => x.Email)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>().ToTable(RefreshTokensTable);
        modelBuilder.Entity<RefreshToken>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(x => x.UserId);
    }
}
