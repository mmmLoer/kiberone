using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

// Only headless helper modes are executed. No desktop startup, services, discovery or VPN.
if (args.Length != 4) throw new ArgumentException("Usage: <old single-file Tutor built with helper> <signed new manifest.json> <downloaded new EXE> <old version>");
var oldExe = Path.GetFullPath(args[0]);
var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(await File.ReadAllTextAsync(args[1]),
    new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
    ?? throw new InvalidDataException("Missing manifest");
AppUpdateInstaller.ValidateManifest("tutor", manifest, args[3]);
var root = Path.Combine(Path.GetTempPath(), "kiberone-update-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var package = await AppUpdateInstaller.StageAsync("tutor", manifest, await File.ReadAllBytesAsync(args[2]), args[3], Path.Combine(root, "stage"));
var appDirectory = Path.Combine(root, "portable");
Directory.CreateDirectory(appDirectory);
var target = Path.Combine(appDirectory, "Kiberone.Tutor.exe");
File.Copy(oldExe, target);
var helper = Path.Combine(root, "helper.exe");
File.Copy(oldExe, helper);
var oldHash = SHA256.HashData(await File.ReadAllBytesAsync(target));
if (!AppUpdateInstaller.CanWriteTargetDirectory(target)) throw new IOException("Portable target is not writable");
var jobPath = Path.Combine(root, "job.json");
await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(new AppUpdateJob(target, package, manifest, int.MaxValue, 0)));
var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
start.ArgumentList.Add("--apply-tutor-update");
start.ArgumentList.Add(jobPath);
using var child = Process.Start(start) ?? throw new IOException("Helper did not start");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await child.WaitForExitAsync(deadline.Token);
var result = File.Exists(jobPath + ".result") ? await File.ReadAllTextAsync(jobPath + ".result") : "missing result";
if (child.ExitCode != 0 || result != "ok") throw new IOException("Headless update failed: " + result);
var installedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target)));
if (!installedHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed hash mismatch");
if (!oldHash.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(target + ".previous")))) throw new IOException("Rollback backup mismatch");
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Root = root, manifest.Version, InstalledHash = installedHash, NoGui = true, NoUac = true }));
