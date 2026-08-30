using System.IO.Pipes;
using System.Text.Json;
using Kiberone.Vpn.WireGuard;

namespace Kiberone.Vpn;

public sealed class VpnBridgeClient
{
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectReadTimeout = TimeSpan.FromSeconds(30);

    public bool IsServiceInstalled
    {
        get
        {
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(VpnBridgeConstants.ServiceName);
                _ = controller.Status;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsServiceRunning
    {
        get
        {
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(VpnBridgeConstants.ServiceName);
                return controller.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryPing()
    {
        try
        {
            return Send(new VpnBridgeRequest(VpnBridgeAction.Ping)).Ok;
        }
        catch
        {
            return false;
        }
    }

    public VpnStatus GetStatus(string configPath) => ToStatus(Send(new VpnBridgeRequest(VpnBridgeAction.Status, configPath)), configPath);

    public VpnStatus InstallConfig(byte[] content, string configPath)
    {
        var response = Send(new VpnBridgeRequest(
            VpnBridgeAction.InstallConfig,
            configPath,
            Convert.ToBase64String(content)));
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "Не удалось установить VPN-конфиг.");
        return ToStatus(response, configPath);
    }

    public VpnStatus Connect(string configPath)
    {
        VpnLog.Info("client", $"Connect request for {configPath}");
        var response = Send(new VpnBridgeRequest(VpnBridgeAction.Connect, configPath));
        var status = ToStatus(response, configPath);
        if (!response.Ok || !response.Connected)
        {
            var message = response.Error ?? status.LastError ?? "Не удалось подключить VPN.";
            VpnLog.Error("client", $"Connect failed: {message}");
            throw new InvalidOperationException(message);
        }

        VpnLog.Info("client", $"Connect succeeded: {configPath}");
        return status;
    }

    public VpnStatus Disconnect(string configPath) => ToStatus(Send(new VpnBridgeRequest(VpnBridgeAction.Disconnect, configPath)), configPath);

    private VpnBridgeResponse Send(VpnBridgeRequest request)
    {
        var readTimeout = request.Action == VpnBridgeAction.Connect ? ConnectReadTimeout : DefaultReadTimeout;
        try
        {
            using var pipe = new NamedPipeClientStream(".", VpnBridgeConstants.PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect((int)PipeConnectTimeout.TotalMilliseconds);
            WriteMessage(pipe, request);
            var readTask = Task.Run(() => ReadMessage(pipe));
            if (!readTask.Wait(readTimeout))
                throw new TimeoutException($"VPN-служба не ответила за {(int)readTimeout.TotalSeconds} с.");

            return readTask.Result ?? new VpnBridgeResponse(false, Error: "Пустой ответ VPN-службы.");
        }
        catch (Exception error)
        {
            VpnLog.Error("client", $"Pipe request {request.Action} failed", error);
            throw;
        }
    }

    private static void WriteMessage(Stream pipe, VpnBridgeRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, VpnBridgeJson.Options);
        var length = BitConverter.GetBytes(payload.Length);
        pipe.Write(length);
        pipe.Write(payload);
        pipe.Flush();
    }

    private static VpnBridgeResponse? ReadMessage(Stream pipe)
    {
        var lengthBytes = ReadExact(pipe, sizeof(int));
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > 1024 * 1024)
            throw new InvalidOperationException("Некорректный ответ VPN-службы.");

        var payload = ReadExact(pipe, length);
        return JsonSerializer.Deserialize<VpnBridgeResponse>(payload, VpnBridgeJson.Options);
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read == 0)
                throw new EndOfStreamException("VPN-служба закрыла соединение.");
            offset += read;
        }
        return buffer;
    }

    private static VpnStatus ToStatus(VpnBridgeResponse response, string configPath) =>
        new(
            response.Connected,
            response.State,
            TunnelService.ServiceNameFromConfig(configPath),
            response.ConfigPath ?? configPath,
            response.ConfigExists || File.Exists(configPath),
            response.Error);
}
