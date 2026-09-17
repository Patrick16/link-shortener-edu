using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Common;

// SHA-256 of the input salted with a fresh GUID, encoded to base62.
// The salt keeps identical inputs from colliding with each other,
// on top of the ~2.2*10^14 keyspace of an 8-char base62 code.
public class Sha256Base62HashGenerator : IHashGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int HashLength = 8;

    public string Generate(string input)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);

        var salted = $"{input}:{Guid.NewGuid():N}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(salted));

        var value = new BigInteger(hashBytes, isUnsigned: true);
        var chars = new char[HashLength];
        for (var i = 0; i < HashLength; i++)
        {
            value = BigInteger.DivRem(value, Alphabet.Length, out var remainder);
            chars[i] = Alphabet[(int)remainder];
        }

        return new string(chars);
    }
}
