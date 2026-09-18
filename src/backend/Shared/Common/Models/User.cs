namespace Common.Models;

public record class User(
    Guid Id,
    string Name,
    string Email,
    string PasswordHash,
    string Sault);
