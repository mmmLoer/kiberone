/* Adapted from WireGuard embeddable-dll-service csharp sample (MIT). */

using System.Runtime.InteropServices;

namespace Kiberone.Vpn.WireGuard;

internal static class Win32
{
    [Flags]
    public enum ScmAccessRights
    {
        Connect = 0x0001,
        CreateService = 0x0002,
        EnumerateService = 0x0004,
        Lock = 0x0008,
        QueryLockStatus = 0x0010,
        ModifyBootConfig = 0x0020,
        StandardRightsRequired = 0xF0000,
        AllAccess = StandardRightsRequired | Connect | CreateService | EnumerateService | Lock | QueryLockStatus | ModifyBootConfig
    }

    [Flags]
    public enum ServiceAccessRights
    {
        QueryConfig = 0x1,
        ChangeConfig = 0x2,
        QueryStatus = 0x4,
        EnumerateDependants = 0x8,
        Start = 0x10,
        Stop = 0x20,
        PauseContinue = 0x40,
        Interrogate = 0x80,
        UserDefinedControl = 0x100,
        Delete = 0x00010000,
        StandardRightsRequired = 0xF0000,
        AllAccess = StandardRightsRequired | QueryConfig | ChangeConfig | QueryStatus | EnumerateDependants | Start | Stop | PauseContinue | Interrogate | UserDefinedControl
    }

    public enum ServiceStartType
    {
        Boot = 0x00000000,
        System = 0x00000001,
        Auto = 0x00000002,
        Demand = 0x00000003,
        Disabled = 0x00000004
    }

    public enum ServiceControl
    {
        Stop = 0x00000001
    }

    public enum ServiceError
    {
        Normal = 0x00000001
    }

    public enum ServiceSidType
    {
        Unrestricted = 0x00000001
    }

    public enum ServiceType
    {
        Win32OwnProcess = 0x00000010
    }

    public enum ServiceState
    {
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4
    }

    public enum ServiceConfigType
    {
        Description = 1,
        SidInfo = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    public class ServiceStatus
    {
        public int dwServiceType;
        public ServiceState dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ServiceDescription
    {
        public string lpDescription;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, ScmAccessRights dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, ServiceAccessRights dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateService(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        ServiceAccessRights dwDesiredAccess,
        ServiceType dwServiceType,
        ServiceStartType dwStartType,
        ServiceError dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string lpDependencies,
        string? lp,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr hService, ServiceControl dwControl, ServiceStatus lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(IntPtr hService, ServiceStatus lpServiceStatus);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2(IntPtr hService, ServiceConfigType dwInfoLevel, ref ServiceSidType lpInfo);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2(IntPtr hService, ServiceConfigType dwInfoLevel, ref ServiceDescription lpInfo);
}
