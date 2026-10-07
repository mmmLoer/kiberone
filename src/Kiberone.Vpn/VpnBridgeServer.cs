using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Vpn.WireGuard;

namespace Kiberone.Vpn;

public sealed class VpnBridgeServer
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        VpnLog.Info("bridge", $"VPN bridge started. Exe={Environment.ProcessPath} Base={AppContext.BaseDirectory}");
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = CreateServer();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                await HandleClientAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException error)
            {
                VpnLog.Warn("bridge", $"Pipe IO error: {error.Message}");
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            VpnBridgeConstants.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            var request = await ReadRequestAsync(pipe, cancellationToken);
            if (request is null)
            {
                await WriteResponseAsync(pipe, new VpnBridgeResponse(false, Error: "Некорректный запрос."), cancellationToken);
                return;
            }

            VpnLog.Info("bridge", $"Request: {request.Action} config={request.ConfigPath ?? VpnOptions.ManagedConfigPath}");
            if (request.Action == VpnBridgeAction.ApplyUpdate && !IsStudentUpdateCaller(pipe, request))
            {
                await WriteResponseAsync(pipe, new VpnBridgeResponse(false, Error: "Обновление разрешено только запущенному Student."), cancellationToken);
                return;
            }
            var response = Execute(request);
            VpnLog.Info("bridge", $"Response: ok={response.Ok} connected={response.Connected} state={response.State} error={response.Error ?? "-"}");
            await WriteResponseAsync(pipe, response, cancellationToken);
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", "Request handling failed", error);
            await WriteResponseAsync(pipe, new VpnBridgeResponse(false, Error: error.Message), cancellationToken);
        }
    }

    private static VpnBridgeResponse Execute(VpnBridgeRequest request)
    {
        if (request.Action == VpnBridgeAction.ApplyUpdate)
            return ApplyUpdate(request);

        string configPath;
        try
        {
            configPath = ResolveConfigPath(request.ConfigPath);
        }
        catch (InvalidOperationException error)
        {
            return new VpnBridgeResponse(false, Error: error.Message);
        }

        return request.Action switch
        {
            VpnBridgeAction.Ping => new VpnBridgeResponse(true, State: "ready"),
            VpnBridgeAction.Status => BuildStatus(configPath),
            VpnBridgeAction.InstallConfig => InstallConfig(request, configPath),
            VpnBridgeAction.Connect => Connect(configPath),
            VpnBridgeAction.Disconnect => Disconnect(configPath),
            _ => new VpnBridgeResponse(false, Error: $"Неизвестное действие: {request.Action}")
        };
    }

    private static VpnBridgeResponse ApplyUpdate(VpnBridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.TargetPath))
            return new VpnBridgeResponse(false, Error: "Нужны source_path и target_path для обновления.");

        if (!VpnOptions.IsAllowedUpdateSourcePath(request.SourcePath))
            return new VpnBridgeResponse(false, Error: "Источник обновления вне разрешённой папки.");

        if (!VpnOptions.IsAllowedUpdateTargetPath(request.TargetPath))
            return new VpnBridgeResponse(false, Error: "Цель обновления вне разрешённой папки Student.");
        if (request.ClientPid is null or <= 0 || request.Update is null ||
            !StudentUpdateSignature.Verify(request.Update.Version, request.Update.Size,
                request.Update.Sha256, request.Update.Signature))
            return new VpnBridgeResponse(false, Error: "Недействительная подпись обновления Student.");

        try
        {
            var source = Path.GetFullPath(request.SourcePath);
            var target = Path.GetFullPath(request.TargetPath);
            var stagedDirectory = Path.Combine(Path.GetDirectoryName(target)!, ".update-staging");
            Directory.CreateDirectory(stagedDirectory);
            var staged = Path.Combine(stagedDirectory, $"student-{Guid.NewGuid():N}.exe");
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);

                string stagedHash;
                using (var verify = File.OpenRead(staged))
                    stagedHash = Convert.ToHexString(SHA256.HashData(verify));
                if (new FileInfo(staged).Length != request.Update.Size ||
                    !stagedHash.Equals(request.Update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SHA-256 обновления не совпадает с проверенным файлом.");

                var marker = source + ".result";
                if (File.Exists(marker)) File.Delete(marker);
                var serviceExe = Environment.ProcessPath;
                var serviceUsesTarget = !string.IsNullOrWhiteSpace(serviceExe)
                    && string.Equals(Path.GetFullPath(serviceExe), target, StringComparison.OrdinalIgnoreCase);
                var script = BuildUpdateHelperScript(staged, target, marker, stagedHash,
                    request.ClientPid.Value, Environment.ProcessId, serviceUsesTarget);
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                using var helper = Process.Start(start);
                if (helper is null)
                    throw new InvalidOperationException("Не удалось запустить системный помощник обновления.");
                VpnLog.Info("bridge", $"Queued update {source} -> {target}, helper PID={helper.Id}");
                return new VpnBridgeResponse(true, State: "queued", ConfigPath: target);
            }
            catch
            {
                if (File.Exists(staged)) File.Delete(staged);
                throw;
            }
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", "ApplyUpdate failed", error);
            return new VpnBridgeResponse(false, Error: error.Message);
        }
    }

    private static bool IsStudentUpdateCaller(NamedPipeServerStream pipe, VpnBridgeRequest request)
    {
        if (request.ClientPid is null or <= 0 || string.IsNullOrWhiteSpace(request.TargetPath)) return false;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != request.ClientPid)
            return false;
        try
        {
            using var process = Process.GetProcessById(request.ClientPid.Value);
            var actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual)
                && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(request.TargetPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private static string BuildUpdateHelperScript(string staged, string target, string marker, string expectedHash,
        int clientPid, int servicePid, bool serviceUsesTarget)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $staged = {{Literal(staged)}}
            $target = {{Literal(target)}}
            $marker = {{Literal(marker)}}
            $expectedHash = {{Literal(expectedHash)}}
            $clientPid = {{clientPid}}
            $servicePid = {{servicePid}}
            $serviceUsesTarget = ${{(serviceUsesTarget ? "true" : "false")}}
            $serviceName = {{Literal(VpnBridgeConstants.ServiceName)}}
            $backup = $target + '.update-backup'
            try {
                $deadline = (Get-Date).AddSeconds(120)
                while (Get-Process -Id $clientPid -ErrorAction SilentlyContinue) {
                    if ((Get-Date) -gt $deadline) { throw 'Student не завершился за 120 секунд.' }
                    Start-Sleep -Seconds 1
                }
                if ($serviceUsesTarget) {
                    Stop-Service -Name $serviceName -ErrorAction Stop
                    $deadline = (Get-Date).AddSeconds(45)
                    while (Get-Process -Id $servicePid -ErrorAction SilentlyContinue) {
                        if ((Get-Date) -gt $deadline) { throw 'Системная служба не завершилась.' }
                        Start-Sleep -Seconds 1
                    }
                }
                Copy-Item -LiteralPath $target -Destination $backup -Force
                Copy-Item -LiteralPath $staged -Destination $target -Force
                $stream = [IO.File]::OpenRead($target)
                $hasher = [Security.Cryptography.SHA256]::Create()
                try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
                finally { $stream.Dispose(); $hasher.Dispose() }
                if ($actualHash -ne $expectedHash) {
                    throw 'SHA-256 установленного файла не совпадает.'
                }
                if ($serviceUsesTarget) { Start-Service -Name $serviceName -ErrorAction Stop }
                [IO.File]::WriteAllText($marker, 'ok')
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            } catch {
                $failure = $_.Exception.Message
                try {
                    if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $target -Force }
                    if ($serviceUsesTarget -and (Get-Service -Name $serviceName).Status -ne 'Running') {
                        Start-Service -Name $serviceName
                    }
                } catch { $failure += '; rollback: ' + $_.Exception.Message }
                [IO.File]::WriteAllText($marker, 'error: ' + $failure)
            } finally {
                Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private static VpnBridgeResponse InstallConfig(VpnBridgeRequest request, string configPath)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigBase64))
            return new VpnBridgeResponse(false, Error: "Пустой VPN-конфиг.");

        byte[] content;
        try
        {
            content = Convert.FromBase64String(request.ConfigBase64);
        }
        catch (FormatException error)
        {
            VpnLog.Error("bridge", "Invalid base64 config", error);
            return new VpnBridgeResponse(false, Error: "Некорректный VPN-конфиг (base64).");
        }

        try
        {
            content = VpnConfigNormalizer.NormalizeForClassroom(content);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllBytes(configPath, content);
            VpnLog.Info("bridge", $"Installed config ({content.Length} bytes) -> {configPath}");
            return BuildStatus(configPath);
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", $"Failed to write config to {configPath}", error);
            return new VpnBridgeResponse(false, Error: error.Message, ConfigPath: configPath);
        }
    }

    private static VpnBridgeResponse Connect(string configPath)
    {
        if (!File.Exists(configPath))
            return new VpnBridgeResponse(false, ConfigPath: configPath, Error: $"VPN config missing: {configPath}");

        try
        {
            EnsureClassroomSafeConfig(configPath);

            var current = TunnelService.GetStatus(configPath);
            if (current.Connected || current.State is "startpending" or "running")
            {
                VpnLog.Info("bridge", $"Restarting existing tunnel: {current.ServiceName} ({current.State})");
                TunnelService.Disconnect(configPath, waitForStop: true);
            }

            VpnLog.Info("bridge", $"Starting tunnel for {configPath}");
            TunnelService.Connect(configPath, ephemeral: false);

            for (var attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(250);
                var status = TunnelService.GetStatus(configPath);
                if (string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    // Give WireGuard a moment to finish handshake/routes before reporting success.
                    Thread.Sleep(1000);
                    status = TunnelService.GetStatus(configPath);
                    if (!string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase))
                        continue;

                    VpnLog.Info("bridge", $"Tunnel running on attempt {attempt + 1}");
                    return BuildStatus(configPath, ok: true);
                }

                if (string.Equals(status.State, "stopped", StringComparison.OrdinalIgnoreCase) && attempt > 4)
                    break;
            }

            var finalStatus = TunnelService.GetStatus(configPath);
            try { TunnelService.Disconnect(configPath, waitForStop: true); } catch { /* best effort */ }
            var message = $"Туннель не поднялся. Состояние: {finalStatus.State}. Лог: {VpnLog.PrimaryLogPath}";
            VpnLog.Warn("bridge", message);
            return new VpnBridgeResponse(
                false,
                Connected: false,
                State: finalStatus.State,
                ConfigPath: configPath,
                ConfigExists: true,
                Error: message);
        }
        catch (Win32Exception error)
        {
            var message = $"Win32 {error.NativeErrorCode}: {error.Message}. Exe={Environment.ProcessPath} Base={AppContext.BaseDirectory}. Лог: {VpnLog.PrimaryLogPath}";
            VpnLog.Error("bridge", "TunnelService.Connect failed", error);
            try { TunnelService.Disconnect(configPath, waitForStop: true); } catch { }
            return new VpnBridgeResponse(false, ConfigPath: configPath, ConfigExists: true, Error: message);
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", "Connect failed", error);
            try { TunnelService.Disconnect(configPath, waitForStop: true); } catch { }
            return new VpnBridgeResponse(false, ConfigPath: configPath, ConfigExists: File.Exists(configPath), Error: error.Message);
        }
    }

    private static VpnBridgeResponse Disconnect(string configPath)
    {
        try
        {
            if (File.Exists(configPath))
                TunnelService.Disconnect(configPath, waitForStop: true);
            VpnLog.Info("bridge", $"Disconnected {configPath}");
            return BuildStatus(configPath, ok: true);
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", "Disconnect failed", error);
            return new VpnBridgeResponse(false, Error: error.Message, ConfigPath: configPath);
        }
    }

    private static void EnsureClassroomSafeConfig(string configPath)
    {
        var original = File.ReadAllText(configPath);
        var normalized = VpnConfigNormalizer.NormalizeForClassroom(original);
        if (string.Equals(original, normalized, StringComparison.Ordinal))
            return;

        File.WriteAllText(configPath, normalized);
        VpnLog.Info("bridge", $"Updated AllowedIPs for classroom LAN access: {configPath}");
    }

    private static VpnBridgeResponse BuildStatus(string configPath, bool ok = true)
    {
        if (!File.Exists(configPath))
        {
            return new VpnBridgeResponse(
                ok,
                Connected: false,
                State: "config_missing",
                ConfigPath: configPath,
                ConfigExists: false);
        }

        var status = TunnelService.GetStatus(configPath);
        return new VpnBridgeResponse(
            ok,
            Connected: status.Connected,
            State: status.State,
            ConfigPath: configPath,
            ConfigExists: true);
    }

    private static string ResolveConfigPath(string? configPath)
    {
        if (!VpnOptions.IsAllowedBridgeConfigPath(configPath))
            throw new InvalidOperationException("VPN config path вне ProgramData\\KIBERone\\Student\\vpn.");

        return string.IsNullOrWhiteSpace(configPath)
            ? VpnOptions.ManagedConfigPath
            : Path.GetFullPath(configPath);
    }

    private static async Task<VpnBridgeRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = await ReadExactAsync(stream, sizeof(int), cancellationToken);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > 1024 * 1024)
            return null;

        var payload = await ReadExactAsync(stream, length, cancellationToken);
        return JsonSerializer.Deserialize<VpnBridgeRequest>(payload, VpnBridgeJson.Options);
    }

    private static async Task WriteResponseAsync(Stream stream, VpnBridgeResponse response, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, VpnBridgeJson.Options);
        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
        return buffer;
    }
}
