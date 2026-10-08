using System.Security.Cryptography;
using System.Text;

namespace Kiberone.Infrastructure;

// Immutable files make enrollment atomic even across server instances. Never overwrite a pin.
public sealed class DeviceCredentials(string directory)
{
    private readonly object gate = new();
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private string PathFor(string id) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToUpperInvariant()))) + ".credential");
    public static bool Valid(string id, string secret) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 160 && !id.Contains(',')
        && secret.Length == 64 && secret.All(Uri.IsHexDigit);

    public string GetOrCreateSecret(string id)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(id);
            if (!File.Exists(path)) WriteNew(path, NewSecret());
            var secret = File.ReadAllText(path);
            if (!Valid(id, secret)) throw new InvalidDataException("Invalid persisted device credential.");
            return secret;
        }
    }

    public bool AuthenticateOrEnroll(string id, string secret)
    {
        if (!Valid(id, secret)) return false;
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(id);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            if (!File.Exists(path)) WriteNew(path, hash);
            var expected = File.ReadAllText(path);
            return expected.Length == hash.Length && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(hash));
        }
    }

    private static void WriteNew(string path, string value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.ASCII.GetBytes(value));
                stream.Flush(true);
            }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
