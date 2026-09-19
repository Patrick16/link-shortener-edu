using AuthApi;
using AuthApi.Controllers;
using AuthApi.Models;
using Common.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace AuthApi.Tests;

public class AuthControllerTests
{
    private static readonly PasswordHasher<User> PasswordHasher = new();

    private static DatabaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DatabaseContext(options);
    }

    private static AuthController NewController(DatabaseContext context, out Mock<IJwtTokenGenerator> tokenGenerator)
    {
        tokenGenerator = new Mock<IJwtTokenGenerator>();
        return new AuthController(context, tokenGenerator.Object);
    }

    [Fact]
    public async Task Register_NewEmail_CreatesUserAndReturnsToken()
    {
        await using var context = NewContext();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        var sut = NewController(context, out var tokenGenerator);
        tokenGenerator.Setup(x => x.GenerateToken(It.IsAny<User>())).Returns(("jwt-token", expiresAt));

        var result = await sut.Register(
            new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = "Str0ngPassw0rd!" },
            CancellationToken.None);

        var response = Assert.IsType<AuthResponse>(result.Value);
        Assert.Equal("jwt-token", response.Token);
        Assert.Equal(expiresAt, response.ExpiresAt);

        var stored = await context.Users.SingleAsync();
        Assert.Equal("alice@example.com", stored.Email);
        Assert.Equal("Alice", stored.Name);
        Assert.NotEqual("Str0ngPassw0rd!", stored.PasswordHash);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflictAndDoesNotIssueToken()
    {
        await using var context = NewContext();
        context.Users.Add(new User(Guid.NewGuid(), "Existing", "alice@example.com", "hash", string.Empty));
        await context.SaveChangesAsync();
        var sut = NewController(context, out var tokenGenerator);

        var result = await sut.Register(
            new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = "Str0ngPassw0rd!" },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
        Assert.Equal(1, await context.Users.CountAsync());
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        await using var context = NewContext();
        var placeholder = new User(Guid.Empty, "Alice", "alice@example.com", string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, "Str0ngPassw0rd!");
        var user = new User(Guid.NewGuid(), "Alice", "alice@example.com", passwordHash, string.Empty);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        var sut = NewController(context, out var tokenGenerator);
        tokenGenerator.Setup(x => x.GenerateToken(It.Is<User>(u => u.Email == "alice@example.com")))
            .Returns(("jwt-token", expiresAt));

        var result = await sut.Login(
            new LoginRequest { Email = "alice@example.com", Password = "Str0ngPassw0rd!" },
            CancellationToken.None);

        var response = Assert.IsType<AuthResponse>(result.Value);
        Assert.Equal("jwt-token", response.Token);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var tokenGenerator);

        var result = await sut.Login(
            new LoginRequest { Email = "ghost@example.com", Password = "whatever" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var placeholder = new User(Guid.Empty, "Alice", "alice@example.com", string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, "Str0ngPassw0rd!");
        context.Users.Add(new User(Guid.NewGuid(), "Alice", "alice@example.com", passwordHash, string.Empty));
        await context.SaveChangesAsync();
        var sut = NewController(context, out var tokenGenerator);

        var result = await sut.Login(
            new LoginRequest { Email = "alice@example.com", Password = "wrong-password" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
    }
}
