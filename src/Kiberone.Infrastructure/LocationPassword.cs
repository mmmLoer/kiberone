using System.Security.Cryptography;
using System.Text;

namespace Kiberone.Infrastructure;

public static class LocationPassword
{
    public const int Iterations = 120_000;

    public static (string Salt, string Hash) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(salt), Hash(password, salt));
    }

    public static bool Verify(string password, string salt, string hash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(hash))
            return false;
        byte[] saltBytes;
        try { saltBytes = Convert.FromBase64String(salt); }
        catch (FormatException) { return false; }
        var actual = Hash(password, saltBytes);
        var expected = hash.Trim();
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    }

    private static string Hash(string password, byte[] salt) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
}

public sealed record LocationSecretRecord(string Location, string Salt, string Hash);
