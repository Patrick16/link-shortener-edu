using AuthApi;
using AuthApi.Controllers;
using AuthApi.Models;
using Common.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    private static AuthController NewController(
        DatabaseContext context,
        out Mock<IJwtTokenGenerator> tokenGenerator,
        out Mock<IRefreshTokenService> refreshTokenService,
        bool isDevelopment = true)
    {
        tokenGenerator = new Mock<IJwtTokenGenerator>();
        refreshTokenService = new Mock<IRefreshTokenService>();

        var environment = new Mock<IWebHostEnvironment>();
        environment.Setup(x => x.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");

        return new AuthController(context, tokenGenerator.Object, refreshTokenService.Object, environment.Object)
        {
            // Request/Response cookie access needs a real HttpContext - the controller has none by
            // default when constructed directly like this, outside an actual HTTP pipeline.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static string? SetCookieHeader(AuthController controller) =>
        controller.ControllerContext.HttpContext.Response.Headers.SetCookie.FirstOrDefault();

    [Fact]
    public async Task Register_NewEmail_CreatesUserAndReturnsToken()
    {
        await using var context = NewContext();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);
        tokenGenerator.Setup(x => x.GenerateToken(It.IsAny<User>())).Returns(("jwt-token", expiresAt));
        refreshTokenService.Setup(x => x.IssueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("raw-refresh-token", DateTime.UtcNow.AddDays(14)));

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

        refreshTokenService.Verify(x => x.IssueAsync(stored.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("raw-refresh-token", SetCookieHeader(sut));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflictAndDoesNotIssueToken()
    {
        await using var context = NewContext();
        context.Users.Add(new User(Guid.NewGuid(), "Existing", "alice@example.com", "hash", string.Empty));
        await context.SaveChangesAsync();
        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);

        var result = await sut.Register(
            new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = "Str0ngPassw0rd!" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
        refreshTokenService.Verify(x => x.IssueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await context.Users.CountAsync());
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenAndSetsRefreshCookie()
    {
        await using var context = NewContext();
        var placeholder = new User(Guid.Empty, "Alice", "alice@example.com", string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, "Str0ngPassw0rd!");
        var user = new User(Guid.NewGuid(), "Alice", "alice@example.com", passwordHash, string.Empty);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);
        tokenGenerator.Setup(x => x.GenerateToken(It.Is<User>(u => u.Email == "alice@example.com")))
            .Returns(("jwt-token", expiresAt));
        refreshTokenService.Setup(x => x.IssueAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("raw-refresh-token", DateTime.UtcNow.AddDays(14)));

        var result = await sut.Login(
            new LoginRequest { Email = "alice@example.com", Password = "Str0ngPassw0rd!" },
            CancellationToken.None);

        var response = Assert.IsType<AuthResponse>(result.Value);
        Assert.Equal("jwt-token", response.Token);
        Assert.Contains("raw-refresh-token", SetCookieHeader(sut));
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);

        var result = await sut.Login(
            new LoginRequest { Email = "ghost@example.com", Password = "whatever" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.Status);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
        refreshTokenService.Verify(x => x.IssueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var placeholder = new User(Guid.Empty, "Alice", "alice@example.com", string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, "Str0ngPassw0rd!");
        context.Users.Add(new User(Guid.NewGuid(), "Alice", "alice@example.com", passwordHash, string.Empty));
        await context.SaveChangesAsync();
        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);

        var result = await sut.Login(
            new LoginRequest { Email = "alice@example.com", Password = "wrong-password" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.Status);
        tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
        refreshTokenService.Verify(x => x.IssueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_NoCookie_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var refreshTokenService);

        var result = await sut.Refresh(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        refreshTokenService.Verify(
            x => x.RotateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_InvalidToken_ReturnsUnauthorizedAndClearsCookie()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var refreshTokenService);
        sut.ControllerContext.HttpContext.Request.Headers.Cookie = "refreshToken=stale-token";
        refreshTokenService.Setup(x => x.RotateAsync("stale-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefreshRotationResult?)null);

        var result = await sut.Refresh(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        var setCookie = SetCookieHeader(sut);
        Assert.Contains("refreshToken=", setCookie);
        Assert.Contains("01 Jan 1970", setCookie);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewAccessTokenAndRotatesCookie()
    {
        await using var context = NewContext();
        var user = new User(Guid.NewGuid(), "Alice", "alice@example.com", "hash", string.Empty);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var sut = NewController(context, out var tokenGenerator, out var refreshTokenService);
        sut.ControllerContext.HttpContext.Request.Headers.Cookie = "refreshToken=old-token";
        var newExpiresAt = DateTime.UtcNow.AddDays(14);
        refreshTokenService.Setup(x => x.RotateAsync("old-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshRotationResult(user.Id, "new-token", newExpiresAt));
        var accessExpiresAt = DateTime.UtcNow.AddHours(1);
        tokenGenerator.Setup(x => x.GenerateToken(It.Is<User>(u => u.Id == user.Id)))
            .Returns(("new-jwt-token", accessExpiresAt));

        var result = await sut.Refresh(CancellationToken.None);

        var response = Assert.IsType<AuthResponse>(result.Value);
        Assert.Equal("new-jwt-token", response.Token);
        Assert.Contains("new-token", SetCookieHeader(sut));
    }

    [Fact]
    public async Task Refresh_UserNoLongerExists_ReturnsUnauthorized()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var refreshTokenService);
        sut.ControllerContext.HttpContext.Request.Headers.Cookie = "refreshToken=old-token";
        refreshTokenService.Setup(x => x.RotateAsync("old-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshRotationResult(Guid.NewGuid(), "new-token", DateTime.UtcNow.AddDays(14)));

        var result = await sut.Refresh(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public async Task Logout_WithCookie_RevokesTokenClearsCookieAndReturnsNoContent()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var refreshTokenService);
        sut.ControllerContext.HttpContext.Request.Headers.Cookie = "refreshToken=some-token";

        var result = await sut.Logout(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        refreshTokenService.Verify(x => x.RevokeAsync("some-token", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("01 Jan 1970", SetCookieHeader(sut));
    }

    [Fact]
    public async Task Logout_NoCookie_ReturnsNoContentWithoutRevoking()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var refreshTokenService);

        var result = await sut.Logout(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        refreshTokenService.Verify(
            x => x.RevokeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
