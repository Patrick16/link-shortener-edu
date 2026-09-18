using AuthApi.Models;
using Common.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthApi.Controllers;

[ApiController]
[Route("/")]
public class AuthController(
    DatabaseContext context,
    IJwtTokenGenerator tokenGenerator) : Controller
{
    private static readonly PasswordHasher<User> PasswordHasher = new();

    private readonly DatabaseContext _context = context;
    private readonly IJwtTokenGenerator _tokenGenerator = tokenGenerator;

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var emailTaken = await _context.Users.AnyAsync(x => x.Email == request.Email, cancellationToken);
        if (emailTaken)
        {
            return Conflict("A user with this email already exists.");
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
            return Unauthorized("Invalid email or password.");
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized("Invalid email or password.");
        }

        var (token, expiresAt) = _tokenGenerator.GenerateToken(user);
        return new AuthResponse(token, expiresAt);
    }
}
