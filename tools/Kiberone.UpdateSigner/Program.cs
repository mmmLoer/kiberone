using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;

if (args.Length is not (3 or 4))
    throw new ArgumentException("Usage: Kiberone.UpdateSigner <exe> <version> <manifest-path> [student|tutor]");

var (exePath, version, manifestPath) = (Path.GetFullPath(args[0]), args[1].Trim(), Path.GetFullPath(args[2]));
var app = args.Length == 4 ? args[3] : "student";
if (app is not ("student" or "tutor")) throw new ArgumentException("Unknown app.");
var keyPath = Environment.GetEnvironmentVariable("KIBERONE_UPDATE_SIGNING_KEY_PATH");
if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
    throw new InvalidOperationException("Set KIBERONE_UPDATE_SIGNING_KEY_PATH to the private PEM key outside this repository.");
if (!File.Exists(exePath) || !AppReleaseVersion.IsValid(version))
    throw new InvalidOperationException("Student EXE or version is invalid.");

var info = new FileInfo(exePath);
string hash;
using (var input = File.OpenRead(exePath))
    hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

using var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(keyPath));
var signature = Convert.ToBase64String(rsa.SignData(
    StudentUpdateSignature.PayloadApp(app, version, info.Length, hash),
    HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
if (!StudentUpdateSignature.VerifyApp(app, version, info.Length, hash, signature))
    throw new InvalidOperationException("Signing key does not match the public key in Kiberone.Core.");

var manifest = new AppUpdateManifest(version, app == "student" ? "KIBERoneStudent.exe" : "KIBERoneTutor.exe", info.Length, hash, DateTimeOffset.UtcNow, signature);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
var temporary = manifestPath + ".tmp";
File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
}));
File.Move(temporary, manifestPath, true);
Console.WriteLine($"Signed {app} {version}: {info.Length} bytes, SHA-256 {hash}");
