using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;

if (args.Length != 3)
    throw new ArgumentException("Usage: Kiberone.UpdateSigner <student-exe> <version> <manifest-path>");

var (exePath, version, manifestPath) = (Path.GetFullPath(args[0]), args[1].Trim(), Path.GetFullPath(args[2]));
var keyPath = Environment.GetEnvironmentVariable("KIBERONE_UPDATE_SIGNING_KEY_PATH");
if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
    throw new InvalidOperationException("Set KIBERONE_UPDATE_SIGNING_KEY_PATH to the private PEM key outside this repository.");
if (!File.Exists(exePath) || !Version.TryParse(version, out _))
    throw new InvalidOperationException("Student EXE or version is invalid.");

var info = new FileInfo(exePath);
string hash;
using (var input = File.OpenRead(exePath))
    hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

using var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(keyPath));
var signature = Convert.ToBase64String(rsa.SignData(
    StudentUpdateSignature.Payload(version, info.Length, hash),
    HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
if (!StudentUpdateSignature.Verify(version, info.Length, hash, signature))
    throw new InvalidOperationException("Signing key does not match the public key in Kiberone.Core.");

var manifest = new AppUpdateManifest(version, "KIBERoneStudent.exe", info.Length, hash, DateTimeOffset.UtcNow, signature);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
var temporary = manifestPath + ".tmp";
File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
}));
File.Move(temporary, manifestPath, true);
Console.WriteLine($"Signed Student {version}: {info.Length} bytes, SHA-256 {hash}");
