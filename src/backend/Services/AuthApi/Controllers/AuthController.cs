using AuthApi.Models;
using Common.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AuthApi.Controllers;

// Every action here - register/login/refresh/logout - shares one per-client-IP fixed-window policy
// (see AuthApiServiceExtensions.AddAuthRateLimiting). No action gets a stricter or looser policy of
// its own: register/login are the ones an outside reviewer actually flagged as unprotected, and
// refresh/logout sit behind the same controller rather than being carved out as an exception.
[ApiController]
[Route("/")]
[EnableRateLimiting(AuthApiServiceExtensions.AuthRateLimitPolicy)]
public class AuthController(
    DatabaseContext context,
    IJwtTokenGenerator tokenGenerator,
    IRefreshTokenService refreshTokenService,
    IWebHostEnvironment environment,
    ILogger<AuthController> logger) : Controller
{
    private static readonly PasswordHasher<User> PasswordHasher = new();
    private const string RefreshTokenCookieName = "refreshToken";

    private readonly DatabaseContext _context = context;
    private readonly IJwtTokenGenerator _tokenGenerator = tokenGenerator;
    private readonly IRefreshTokenService _refreshTokenService = refreshTokenService;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly ILogger<AuthController> _logger = logger;

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedEmail = NormalizeEmail(request.Email);
        var emailTaken = await _context.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken);
        if (emailTaken)
        {
            // Debug, not Warning - a duplicate registration attempt is an everyday user mistake, not
            // a system concern. Never logging the email itself here or below (PII).
            _logger.LogDebug("Registration rejected: email already registered");
            return EmailAlreadyRegistered();
        }

        // PasswordHasher.HashPassword needs a user instance for context but doesn't read its fields;
        // the real one (with its real Id) isn't in the database yet, so this is a throwaway stand-in.
        var placeholder = new User(Guid.Empty, request.Name, normalizedEmail, string.Empty, string.Empty);
        var passwordHash = PasswordHasher.HashPassword(placeholder, request.Password);

        // PasswordHasher's own hash already embeds a random salt (PBKDF2, self-describing format) —
        // Salt is unused here; kept as-is since it's an existing field on a shared model, not something
        // this change should redefine on its own. Worth revisiting if you want it removed.
        var user = new User(Guid.NewGuid(), request.Name, normalizedEmail, passwordHash, string.Empty);

        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The AnyAsync check above and this insert aren't atomic: two requests for the same email
            // can both pass the check before either commits. The unique index on Email (DatabaseContext)
            // then rejects the loser here instead of at the check, so translate that into the same 409
            // the check normally returns rather than letting it surface as an unhandled 500.
            _logger.LogDebug("Registration rejected: email already registered (race with a concurrent request)");
            return EmailAlreadyRegistered();
        }

        await IssueRefreshCookieAsync(user.Id, cancellationToken);
        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        _logger.LogInformation("Registered user {UserId}", user.Id);
        return new AuthResponse(token, expiresAt);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await _context.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail, cancellationToken);
        // Same "invalid email or password" response whether the email doesn't exist or the password
        // is wrong — telling those apart lets an attacker enumerate registered emails. Same reasoning
        // applies to the log line below: it never includes the email either, for the same reason.
        if (user is null)
        {
            _logger.LogDebug("Login rejected: unknown email");
            return InvalidCredentials();
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            _logger.LogDebug("Login rejected for user {UserId}: wrong password", user.Id);
            return InvalidCredentials();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // PasswordHasher signals this when the stored hash used older/weaker parameters than
            // its current default (e.g. a future .NET upgrade bumping the iteration count).
            // Verification is the only moment the plaintext password is available, so this is the
            // only place the stored hash can ever be upgraded. PasswordHash is an init-only record
            // property - EF Core writes it directly via Entry().Property() regardless, bypassing
            // the C#-only init restriction.
            _context.Entry(user).Property(x => x.PasswordHash).CurrentValue =
                PasswordHasher.HashPassword(user, request.Password);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await IssueRefreshCookieAsync(user.Id, cancellationToken);
        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        _logger.LogInformation("User {UserId} logged in", user.Id);
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

    private ObjectResult EmailAlreadyRegistered() =>
        Problem(
            detail: "A user with this email already exists.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Email already registered.");

    // Postgres string comparison is case-sensitive by default; normalizing on both write (Register)
    // and read (Login, the duplicate-email check) keeps "Alice@x.com" and "alice@x.com" as one account
    // instead of two, matching how users actually think of email addresses.
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
