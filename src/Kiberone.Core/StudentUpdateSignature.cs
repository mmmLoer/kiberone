using System.Security.Cryptography;
using System.Text;

namespace Kiberone.Core;

public static class StudentUpdateSignature
{
    // Public verification key only. Keep the matching private signing key outside the repository.
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAzyQMgN1H3QCVf+V5H9wF
        h4L7XXoKfrRCCqbY3adVX43SOcLjgXzTNoxLcTAnaUGDHzQx30hv4GzYG+XM7bVl
        lnj0C6zdKL6MythOXyN/qoWm0f8W+FNLecLTF9858iazdxSvpji6LWi1smlQv+7d
        p4YsQmmuCa5r4YIY/asv6VHqLvdLQLz0guGMoZkZP00ZQDPfzVOP1Ll86jJ/zVIw
        0JkHiAMONWSkblrbKNUHNjpQsNe3KYX9WdgnQ/LpJIg+dBERCQ2ygfULwjK4RS8m
        S3JrLubx+s+igA/x3yfH9og4T0uT/qLUUWEyUXdsf/vGA8r3ivrojNPdTQNZmRYH
        dWRN8oJ9DjK3z81rFaWpWJvqwiH8Hw4x7BJ13mS151OxPo5PKw44iU4THgmC3jkY
        vDXveA1F0h2UZmPIkVtozFQKfTI32I1KwMRB6eNiDiqNNNDysonmJZSoEGJu9HSk
        2Iq/U0AmefJb5Ugd7sFinzurl29ARg2rKrHWoa5BidT1AgMBAAE=
        -----END PUBLIC KEY-----
        """;

    public static byte[] Payload(string version, long size, string sha256) =>
        Encoding.UTF8.GetBytes($"KIBERoneStudent\n{version.Trim()}\n{size}\n{sha256.Trim().ToLowerInvariant()}\n");

    public static bool Verify(string version, long size, string sha256, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(sha256) || size <= 0 || sha256.Length != 64 ||
            !sha256.All(Uri.IsHexDigit))
            return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKey);
            return rsa.VerifyData(Payload(version, size, sha256), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
