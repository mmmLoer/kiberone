using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record AppUpdateJob(string Target, string Package, AppUpdateManifest Manifest, int ParentPid, long ParentStartTicks);

// Standalone EXE updates only. The copied helper runs before the desktop mutex/UI starts.
public static class AppUpdateInstaller
{
    public const long MaxPackageBytes = 1024L * 1024 * 1024;
    public static void ValidateManifest(string app, AppUpdateManifest manifest, string currentVersion)
    {
        if (!AppReleaseVersion.IsNewer(manifest.Version, currentVersion))
            throw new InvalidDataException("Обновление должно быть новее и из того же канала.");
        if (manifest.Size <= 0 || manifest.Size > MaxPackageBytes
            || Path.GetFileName(manifest.Filename) != manifest.Filename
            || manifest.Filename.Contains('\\') || manifest.Filename.Contains('/')
            || !manifest.Filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Нужен отдельный EXE-файл обновления допустимого размера.");
        if (!StudentUpdateSignature.VerifyApp(app, manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature))
            throw new InvalidDataException("Недействительная подпись обновления.");
    }

    public static async Task<string> StageAsync(string app, AppUpdateManifest manifest, byte[] bytes,
        string currentVersion, string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateManifest(app, manifest, currentVersion);
        if (bytes.LongLength != manifest.Size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Размер или SHA-256 обновления не совпадает с манифестом.");
        var folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "package.exe");
        try { await File.WriteAllBytesAsync(path, bytes, ct); return path; }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Empty assembly Location intentionally detects a bundled single-file runtime.")]
    public static bool IsRunningSingleFileBundle() => string.IsNullOrEmpty(typeof(AppUpdateInstaller).Assembly.Location);

    public static bool IsTutorExecutable(string path) =>
        Path.GetFileName(path).Equals("Kiberone.Tutor.exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("KiberoneTutor.exe", StringComparison.OrdinalIgnoreCase);

    public static async Task BeginTutorInstallAsync(string package, AppUpdateManifest manifest, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Обновление поддерживается в Windows.");
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден EXE Tutor.");
        if (!IsTutorExecutable(target)) throw new InvalidOperationException("Запустите установленный отдельный EXE Tutor.");
        // Detect the running bundle, not stale DLLs left behind by an older installer.
        if (!IsRunningSingleFileBundle())
            throw new InvalidOperationException("Эта сборка не является отдельным EXE. Используйте установщик Tutor.");
        ValidateManifest("tutor", manifest, BuildInfo.Version);
        var folder = Path.GetDirectoryName(package)!;
        var helper = Path.Combine(folder, "Kiberone.Tutor.UpdateHelper.exe");
        File.Copy(target, helper, false);
        using var parent = Process.GetCurrentProcess();
        var job = new AppUpdateJob(target, package, manifest, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        var jobPath = Path.Combine(folder, "install.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), ct);
        using var restart = Process.Start(HelperStart(helper, "--restart-tutor-update", jobPath, false))
            ?? throw new InvalidOperationException("Не удалось запустить перезапуск Tutor.");
        try
        {
            // Portable copies need no UAC. Protected installs use the explicit Windows consent.
            using var apply = Process.Start(HelperStart(helper, "--apply-tutor-update", jobPath, !CanWriteTargetDirectory(target)))
                ?? throw new InvalidOperationException("Не удалось запустить установку.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(jobPath + ".ready"))
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(jobPath + ".result")) throw new IOException(await File.ReadAllTextAsync(jobPath + ".result", ct));
                if (apply.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(45))
                    throw new IOException("Установка не подготовлена. Tutor остаётся открыт.");
                await Task.Delay(100, ct);
            }
        }
        catch
        {
            await File.WriteAllTextAsync(jobPath + ".cancel", "cancel", CancellationToken.None);
            throw;
        }
    }

    public static string TutorInstallStatus(string target) => CanWriteTargetDirectory(target)
        ? "Обновление проверено. Подготавливаем перезапуск Tutor…"
        : "Папка Tutor защищена. Подтвердите разрешение администратора в окне Windows; при отмене Tutor останется открыт.";
    public static bool CanWriteTargetDirectory(string target)
    {
        var probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".kiberone-update-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Headless, portable apply API: production helpers and tests share signature/content validation.
    // This method does not elevate, stop processes, restart a GUI or interact with any service.
    public static async Task ApplyVerifiedPackageAsync(string app, AppUpdateManifest manifest, string currentVersion,
        string package, string target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateManifest(app, manifest, currentVersion);
        var targetName = Path.GetFileName(target);
        if (!Path.IsPathFullyQualified(target) || (app == "tutor" ? !IsTutorExecutable(target)
            : app != "student" || !(targetName.Equals("KIBERoneStudent.exe", StringComparison.OrdinalIgnoreCase)
                || targetName.Equals("Kiberone.Student.exe", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Файл назначения не соответствует приложению.");
        var candidate = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyVerifiedPackageAsync(package, candidate, manifest, ct);
            ct.ThrowIfCancellationRequested();
            File.Replace(candidate, target, target + ".previous", true);
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }

    private static async Task CopyVerifiedPackageAsync(string package, string candidate, AppUpdateManifest manifest, CancellationToken ct)
    {
        using var input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != manifest.Size) throw new InvalidDataException("Размер обновления изменился.");
        using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await input.CopyToAsync(output, ct); output.Flush(true); }
        using var verify = File.OpenRead(candidate);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 обновления изменился.");
    }
    private static ProcessStartInfo HelperStart(string helper, string mode, string jobPath, bool elevated)
    {
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helper)!
        };
        if (elevated) start.Verb = "runas";
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(jobPath);
        return start;
    }

    public static bool TryRunHelper(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--apply-tutor-update" or "--restart-tutor-update")) return false;
        if (args.Length != 2) { Environment.ExitCode = 1; return true; }
        try
        {
            if (args[0] == "--apply-tutor-update") ApplyTutorAsync(args[1]).GetAwaiter().GetResult();
            else RestartTutorAsync(args[1]).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            try { File.WriteAllText(args[1] + ".result", "error: " + error.Message); } catch { }
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static AppUpdateJob ReadJob(string path) => JsonSerializer.Deserialize<AppUpdateJob>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Пустое задание обновления.");

    private static Process? Parent(AppUpdateJob job)
    {
        try
        {
            var process = Process.GetProcessById(job.ParentPid);
            if (process.StartTime.ToUniversalTime().Ticks == job.ParentStartTicks && !process.HasExited) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        return null;
    }

    private static async Task ApplyTutorAsync(string jobPath)
    {
        var job = ReadJob(jobPath);
        ValidateManifest("tutor", job.Manifest, BuildInfo.Version);
        if (!IsTutorExecutable(job.Target) || !Path.IsPathFullyQualified(job.Target))
            throw new InvalidDataException("Некорректный путь Tutor.");
        // The elevated helper may only replace the same binary it was copied from.
        using (var original = File.OpenRead(job.Target))
        using (var self = File.OpenRead(Environment.ProcessPath!))
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(self)))
                throw new InvalidDataException("Установленный Tutor изменился. Повторите проверку обновлений.");
        var candidate = job.Target + ".update-" + Guid.NewGuid().ToString("N");
        var backup = job.Target + ".previous";
        try
        {
            await CopyVerifiedPackageAsync(job.Package, candidate, job.Manifest, CancellationToken.None);
            File.WriteAllText(jobPath + ".ready", "ready");
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (File.Exists(jobPath + ".cancel")) throw new OperationCanceledException("Установка отменена.");
                using var process = Parent(job);
                if (process is null) break;
                if (deadline.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("Tutor не завершился.");
                await Task.Delay(200);
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(candidate, job.Target, backup, true); break; }
                catch (IOException) when (attempt < 30) { await Task.Delay(500); }
            }
            File.WriteAllText(jobPath + ".result", "ok");
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }

    private static async Task RestartTutorAsync(string jobPath)
    {
        var job = ReadJob(jobPath);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(jobPath + ".result"))
        {
            if (File.Exists(jobPath + ".cancel")) return;
            if (deadline.Elapsed > TimeSpan.FromMinutes(5)) break;
            await Task.Delay(200);
        }
        // If installation failed before shutdown, leave the existing desktop instance alone.
        using var parent = Parent(job);
        if (parent is not null) return;
        // Replacement errors leave the original EXE in place. Restart that same target, with
        // bounded retries for transient antivirus locks after the elevated helper exits.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var restarted = Process.Start(new ProcessStartInfo(job.Target)
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(job.Target)! });
                if (restarted is null) throw new IOException("Не удалось перезапустить Tutor.");
                File.WriteAllText(jobPath + ".restart", "started");
                return;
            }
            catch (Exception error) when (attempt < 20 && error is System.ComponentModel.Win32Exception or IOException)
            { await Task.Delay(500); }
        }
    }
}
