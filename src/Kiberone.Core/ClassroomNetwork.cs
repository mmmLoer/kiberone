using System.Text.Json;

namespace Kiberone.Core;

public static class BuildInfo
{
    public const string Version = "0.10.10";
}

public sealed record HeartbeatRequest(
    string ClientId,
    string PcNumber,
    string Hostname,
    string WatchFolder,
    string AppVersion,
    Guid? StudentId,
    Guid? SessionId,
    ClientRuntimeInfo Extra);

public sealed record ClientRuntimeInfo(
    bool WatchdogActive,
    bool FocusModeActive,
    string ActiveApp,
    int? BatteryPercent,
    bool VpnConnected = false,
    bool ScreenLocked = false,
    int? VpnPingMs = null,
    string? VpnRegion = null);

public sealed record VpnRuntimeInfo(
    bool Connected,
    bool Healthy,
    int? PingMs = null,
    string? Region = null,
    string? CheckHost = null,
    string? Error = null);

public sealed record HeartbeatResponse(
    bool Ok,
    DateTimeOffset ServerTime,
    int HeartbeatSeconds,
    int SyncSeconds,
    StudentUpdateInfo? StudentUpdate,
    string? PreferredGroupName = null,
    string? SaveModule = null,
    string? SaveStudentName = null);

public sealed record StudentUpdateInfo(string Version, string Sha256, long Size);

public sealed record ClassroomClientSnapshot(
    string ClientId,
    string PcNumber,
    string Hostname,
    string WatchFolder,
    string AppVersion,
    Guid? StudentId,
    Guid? SessionId,
    ClientRuntimeInfo Extra,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    bool IsOnline);

public sealed record EnqueueCommandRequest(
    IReadOnlyList<string> ClientIds,
    string Kind,
    JsonElement Payload,
    int? TtlSeconds = null);

public sealed record ClassroomCommand(
    Guid Id,
    string Kind,
    JsonElement Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record CommandAcknowledgement(Guid CommandId, bool Succeeded, string? Error = null);

public sealed record CommandReceipt(
    Guid CommandId,
    string ClientId,
    bool Succeeded,
    string? Error,
    DateTimeOffset AcknowledgedAt);

public sealed record CommandRolloutItem(string ClientId, string State, string? Detail);

public sealed record DiscoveryBeacon(
    string Type,
    string Token,
    string Host,
    int Port,
    string ServerId,
    string Version);

public static class ClassroomCommandKinds
{
    public const string Message = "message";
    public const string OpenUrl = "open_url";
    public const string LockScreen = "lock_screen";
    public const string UnlockScreen = "unlock_screen";
    public const string FocusOn = "focus_on";
    public const string FocusOff = "focus_off";
    public const string SyncNow = "sync_now";
    public const string TypingStart = "typing_start";
    public const string TypingFinish = "typing_finish";
    public const string Configure = "configure";
    public const string WatchdogOn = "watchdog_on";
    public const string WatchdogOff = "watchdog_off";
    public const string QuizStart = "quiz_start";
    public const string Notification = "notification";
    public const string VpnConnect = "vpn_connect";
    public const string VpnDisconnect = "vpn_disconnect";
    public const string VpnStatus = "vpn_status";
    public const string VpnInstallConfig = "vpn_install_config";
    public const string SetWorkspace = "set_workspace";
    public const string InstallStarterPack = "install_starter_pack";
    public const string SetWallpaper = "set_wallpaper";

    public static IReadOnlySet<string> SafeKnownKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Message, OpenUrl, LockScreen, UnlockScreen, FocusOn, FocusOff,
        SyncNow, TypingStart, TypingFinish, Configure, WatchdogOn, WatchdogOff, QuizStart, Notification,
        VpnConnect, VpnDisconnect, VpnStatus, VpnInstallConfig, SetWorkspace,
        InstallStarterPack, SetWallpaper
    };
}

public sealed record ClientEventRequest(string ClientId, string Event);
