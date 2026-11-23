using Guardian.Core.Ports;
using Guardian.Windows.Interop;

namespace Guardian.Windows.SystemState;

/// <summary>系统状态读取器（只读探测）。</summary>
public sealed class SystemStateReader : ISystemStateReader
{
    public SystemPowerReading ReadPower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return new SystemPowerReading(false, null, GetMonitorCount());
        }

        var acOnline = status.ACLineStatus == 1;
        int? battery = status.BatteryFlag == 128 || status.BatteryLifePercent == 255
            ? null
            : status.BatteryLifePercent;
        return new SystemPowerReading(acOnline, battery, GetMonitorCount());
    }

    public bool IsRemoteSession()
        => NativeMethods.GetSystemMetrics(Win32Constants.SM_REMOTESESSION) != 0;

    public DateTimeOffset GetSystemBootTime()
    {
        var uptimeMs = NativeMethods.GetTickCount64();
        return DateTimeOffset.Now - TimeSpan.FromMilliseconds(uptimeMs);
    }

    public static int GetMonitorCount()
        => NativeMethods.GetSystemMetrics(Win32Constants.SM_CMONITORS);
}
