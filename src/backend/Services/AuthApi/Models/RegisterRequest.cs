using System.ComponentModel.DataAnnotations;

namespace AuthApi.Models;

public class RegisterRequest
{
    public required string Name { get; init; }
    public required string Email { get; init; }

    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    public required string Password { get; init; }
}
