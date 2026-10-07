using System.Security.Cryptography;
using System.Text;

namespace Kiberone.Core;

public static class VpnConfigIdentity
{
    // A WireGuard private key identifies the peer even when comments, routes or DNS change.
    // Only its digest leaves Tutor; never send or log the key itself.
    public static string Fingerprint(string config)
    {
        var key = VpnConfigText.ReadAssignment(config, "PrivateKey");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("В VPN-конфиге отсутствует PrivateKey.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
    }
}
