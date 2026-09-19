using System.IdentityModel.Tokens.Jwt;
using AuthApi;
using Common.Models;
using Microsoft.Extensions.Configuration;

namespace AuthApi.Tests;

public class JwtTokenGeneratorTests
{
    private static IConfiguration Config(int? expiryMinutes = null)
    {
        var data = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "this-is-a-test-signing-key-that-is-long-enough",
            ["Jwt:Issuer"] = "test-issuer",
            ["Jwt:Audience"] = "test-audience",
        };
        if (expiryMinutes is not null)
        {
            data["Jwt:ExpiryMinutes"] = expiryMinutes.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static User TestUser() => new(Guid.NewGuid(), "Alice", "alice@example.com", "hash", string.Empty);

    [Fact]
    public void GenerateToken_ProducesTokenWithExpectedClaims()
    {
        var sut = new JwtTokenGenerator(Config());
        var user = TestUser();

        var (token, _) = sut.GenerateToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(user.Email, jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal(user.Name, jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Name).Value);
        Assert.Equal("test-issuer", jwt.Issuer);
        Assert.Contains("test-audience", jwt.Audiences);
    }

    [Fact]
    public void GenerateToken_DefaultExpiry_IsSixtyMinutes()
    {
        var sut = new JwtTokenGenerator(Config());
        var before = DateTime.UtcNow;

        var (_, expiresAt) = sut.GenerateToken(TestUser());

        Assert.InRange(expiresAt, before.AddMinutes(59), before.AddMinutes(61));
    }

    [Fact]
    public void GenerateToken_CustomExpiry_IsRespected()
    {
        var sut = new JwtTokenGenerator(Config(expiryMinutes: 5));
        var before = DateTime.UtcNow;

        var (_, expiresAt) = sut.GenerateToken(TestUser());

        Assert.InRange(expiresAt, before.AddMinutes(4), before.AddMinutes(6));
    }

    [Fact]
    public void GenerateToken_MissingSigningKey_Throws()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var sut = new JwtTokenGenerator(config);

        Assert.Throws<ArgumentNullException>(() => sut.GenerateToken(TestUser()));
    }
}
