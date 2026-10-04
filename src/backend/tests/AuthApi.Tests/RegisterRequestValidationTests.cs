using System.ComponentModel.DataAnnotations;
using AuthApi.Models;

namespace AuthApi.Tests;

public class RegisterRequestValidationTests
{
    private static bool TryValidate(RegisterRequest request, out List<ValidationResult> results)
    {
        results = [];
        var context = new ValidationContext(request);
        return Validator.TryValidateObject(request, context, results, validateAllProperties: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void Password_ShorterThanMinimum_FailsValidation(string password)
    {
        var request = new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = password };

        var isValid = TryValidate(request, out var results);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Password)));
    }

    [Fact]
    public void Password_AtLeastMinimumLength_PassesValidation()
    {
        var request = new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = "12345678" };

        var isValid = TryValidate(request, out _);

        Assert.True(isValid);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    public void Email_NotAValidAddress_FailsValidation(string email)
    {
        // Regression: a whitespace-only email used to pass every check up to this point (required
        // only checks presence) and normalize down to "" in AuthController, permanently occupying
        // the unique Email index as a real "address" with no actual email semantics.
        var request = new RegisterRequest { Name = "Alice", Email = email, Password = "12345678" };

        var isValid = TryValidate(request, out var results);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Email)));
    }

    [Fact]
    public void Email_ValidAddress_PassesValidation()
    {
        var request = new RegisterRequest { Name = "Alice", Email = "alice@example.com", Password = "12345678" };

        var isValid = TryValidate(request, out _);

        Assert.True(isValid);
    }
}
