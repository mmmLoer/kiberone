using System.Runtime.InteropServices;

namespace Kiberone.Student;

internal static class BatteryInfo
{
    public static int? TryGetBatteryPercent()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return null;
            // AC line and no battery → BatteryFlag 128 (no system battery)
            if ((status.BatteryFlag & 128) != 0)
                return null;
            if (status.BatteryLifePercent is > 100)
                return null;
            return status.BatteryLifePercent;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);
}
