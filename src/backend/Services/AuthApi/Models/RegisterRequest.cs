using System.ComponentModel.DataAnnotations;

namespace AuthApi.Models;

public class RegisterRequest
{
    public required string Name { get; init; }

    // Also rejects whitespace-only input (e.g. "   ") before it reaches AuthController's
    // NormalizeEmail, which would otherwise trim it down to "" and let it occupy the unique Email
    // index as a real, permanently-reserved "address" with no actual email semantics.
    [EmailAddress(ErrorMessage = "Email must be a valid email address.")]
    public required string Email { get; init; }

    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    public required string Password { get; init; }
}
