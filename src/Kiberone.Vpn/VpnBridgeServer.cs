using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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
        var configPath = ResolveConfigPath(request.ConfigPath);
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
            if (current.Connected)
            {
                VpnLog.Info("bridge", $"Tunnel already running: {current.ServiceName}");
                return BuildStatus(configPath, ok: true);
            }

            VpnLog.Info("bridge", $"Starting tunnel for {configPath}");
            TunnelService.Connect(configPath, ephemeral: false);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(250);
                var status = TunnelService.GetStatus(configPath);
                if (status.Connected)
                {
                    VpnLog.Info("bridge", $"Tunnel connected on attempt {attempt + 1}: {status.State}");
                    return BuildStatus(configPath, ok: true);
                }
            }

            var finalStatus = TunnelService.GetStatus(configPath);
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
            return new VpnBridgeResponse(false, ConfigPath: configPath, ConfigExists: true, Error: message);
        }
        catch (Exception error)
        {
            VpnLog.Error("bridge", "Connect failed", error);
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

    private static string ResolveConfigPath(string? configPath) =>
        string.IsNullOrWhiteSpace(configPath) ? VpnOptions.ManagedConfigPath : Path.GetFullPath(configPath);

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
