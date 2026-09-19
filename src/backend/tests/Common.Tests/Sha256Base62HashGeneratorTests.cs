using Common;

namespace Common.Tests;

public class Sha256Base62HashGeneratorTests
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly Sha256Base62HashGenerator _sut = new();

    [Fact]
    public void Generate_ReturnsEightCharacterCode()
    {
        var hash = _sut.Generate("https://example.com");

        Assert.Equal(8, hash.Length);
    }

    [Fact]
    public void Generate_OnlyUsesBase62Alphabet()
    {
        var hash = _sut.Generate("https://example.com");

        Assert.All(hash, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void Generate_SameInputTwice_ProducesDifferentHashes()
    {
        // The GUID salt means identical input must not collide with itself across calls.
        var first = _sut.Generate("https://example.com");
        var second = _sut.Generate("https://example.com");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Generate(null!));
    }

    [Fact]
    public void Generate_EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.Generate(string.Empty));
    }
}
