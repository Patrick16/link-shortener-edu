using AuthApi.Models;
using Common.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthApi.Controllers;

[ApiController]
[Route("/")]
public class AuthController(
    DatabaseContext context,
    IJwtTokenGenerator tokenGenerator,
    IRefreshTokenService refreshTokenService,
    IWebHostEnvironment environment) : Controller
{
    private static readonly PasswordHasher<User> PasswordHasher = new();
    private const string RefreshTokenCookieName = "refreshToken";

    private readonly DatabaseContext _context = context;
    private readonly IJwtTokenGenerator _tokenGenerator = tokenGenerator;
    private readonly IRefreshTokenService _refreshTokenService = refreshTokenService;
    private readonly IWebHostEnvironment _environment = environment;

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var emailTaken = await _context.Users.AnyAsync(x => x.Email == request.Email, cancellationToken);
        if (emailTaken)
        {
            return Problem(
                detail: "A user with this email already exists.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Email already registered.");
        }

        // PasswordHasher.HashPassword needs a user instance for context but doesn't read its fields;
        // the real one (with its real Id) isn't in the database yet, so this is a throwaway stand-in.
        var placeholder = new User(Guid.Empty, request.Name, request.Email, string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, request.Password);

        // PasswordHasher's own hash already embeds a random salt (PBKDF2, self-describing format) —
        // Sault is unused here; kept as-is since it's an existing field on a shared model, not something
        // this change should redefine on its own. Worth revisiting if you want it removed.
        var user = new User(Guid.NewGuid(), request.Name, request.Email, passwordHash, string.Empty);

        _context.Users.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        await IssueRefreshCookieAsync(user.Id, cancellationToken);
        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        return new AuthResponse(token, expiresAt);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _context.Users.FirstOrDefaultAsync(x => x.Email == request.Email, cancellationToken);
        // Same "invalid email or password" response whether the email doesn't exist or the password
        // is wrong — telling those apart lets an attacker enumerate registered emails.
        if (user is null)
        {
            return InvalidCredentials();
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return InvalidCredentials();
        }

        await IssueRefreshCookieAsync(user.Id, cancellationToken);
        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        return new AuthResponse(token, expiresAt);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(RefreshTokenCookieName, out var rawToken) || string.IsNullOrEmpty(rawToken))
        {
            return InvalidRefreshToken();
        }

        var rotated = await _refreshTokenService.RotateAsync(rawToken, cancellationToken);
        if (rotated is null)
        {
            Response.Cookies.Delete(RefreshTokenCookieName, NewRefreshCookieOptions());
            return InvalidRefreshToken();
        }

        var user = await _context.Users.FindAsync([rotated.UserId], cancellationToken);
        if (user is null)
        {
            Response.Cookies.Delete(RefreshTokenCookieName, NewRefreshCookieOptions());
            return InvalidRefreshToken();
        }

        SetRefreshCookie(rotated.RawToken, rotated.ExpiresAt);

        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        return new AuthResponse(token, expiresAt);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (Request.Cookies.TryGetValue(RefreshTokenCookieName, out var rawToken) && !string.IsNullOrEmpty(rawToken))
        {
            await _refreshTokenService.RevokeAsync(rawToken, cancellationToken);
        }

        Response.Cookies.Delete(RefreshTokenCookieName, NewRefreshCookieOptions());
        return NoContent();
    }

    private async Task IssueRefreshCookieAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (rawToken, expiresAt) = await _refreshTokenService.IssueAsync(userId, cancellationToken);
        SetRefreshCookie(rawToken, expiresAt);
    }

    private void SetRefreshCookie(string rawToken, DateTime expiresAt)
    {
        var options = NewRefreshCookieOptions();
        options.Expires = expiresAt;
        Response.Cookies.Append(RefreshTokenCookieName, rawToken, options);
    }

    // Secure requires HTTPS, which this service doesn't terminate in the docker-compose dev
    // setup (see Program.cs) - only require it outside Development so the cookie still round-trips
    // locally, same trade-off UseHttpsRedirection already makes there.
    private CookieOptions NewRefreshCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = !_environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Path = "/",
    };

    private ObjectResult InvalidRefreshToken() =>
        Problem(
            detail: "Refresh token is missing, expired, or invalid.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Authentication failed.");

    private ObjectResult InvalidCredentials() =>
        Problem(
            detail: "Invalid email or password.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Authentication failed.");
}
