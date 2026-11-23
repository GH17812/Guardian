using System.Runtime.InteropServices;

namespace Guardian.Windows.Display;

/// <summary>
/// DDC/CI 显示器背光控制器（dxva2 MonitorEnergySaver API 系列）。
/// 相比 SC_MONITORPOWER 方案的优势：
/// - 直接关闭显示器背光（VCP_POWER soft-off），系统电源状态不变；
/// - 串流软件不会感知"显示器熄灭"，不会被其周期性点亮请求唤醒；
/// - 输入事件不唤醒背光，无亮/灭切换抖动（串流端无卡顿）；
/// - 限制：依赖显示器支持 DDC/CI（多数外接显示器支持，笔记本内屏不支持）。
/// </summary>
internal static class DdcCiController
{
    private const byte VCP_POWER = 0xD6;
    private const uint POWER_ON = 1;
    private const uint POWER_SOFT_OFF = 4; // soft off：DDC 通道保持可用，可远程恢复

    public static bool TurnOffAll() => ForEachMonitor(h => SetVCPFeature(h, VCP_POWER, POWER_SOFT_OFF));

    public static bool TurnOnAll() => ForEachMonitor(h => SetVCPFeature(h, VCP_POWER, POWER_ON));

    /// <summary>探测是否存在支持 DDC/CI 的物理显示器（读取当前 VCP 值验证通道可用）。</summary>
    public static bool IsDdcCiAvailable()
    {
        var supported = false;
        ForEachMonitor(h =>
        {
            if (GetVCPFeatureAndVCPFeatureReply(h, VCP_POWER, IntPtr.Zero, out _, out _))
            {
                supported = true;
            }
            return supported;
        });
        return supported;
    }

    private static bool ForEachMonitor(Func<IntPtr, bool> action)
    {
        var anySuccess = false;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                return true;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            {
                return true;
            }

            try
            {
                foreach (var m in monitors)
                {
                    try
                    {
                        if (action(m.hPhysicalMonitor))
                        {
                            anySuccess = true;
                        }
                    }
                    catch
                    {
                        // 单个显示器失败不阻断其他显示器。
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
            return true;
        }, IntPtr.Zero);

        return anySuccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, IntPtr pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);
}
