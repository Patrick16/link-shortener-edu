namespace AuthApi.Models;

public record AuthResponse(string Token, DateTime ExpiresAt);
