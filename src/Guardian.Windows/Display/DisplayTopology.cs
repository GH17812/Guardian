using System.Runtime.InteropServices;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Display;

/// <summary>
/// 活动显示器拓扑读取：识别“物理屏是否已被外部（如串流虚拟显示器方案）停用”。
/// Sunshine 的 ensure_only_display 在串流期间会停用全部物理显示器，只保留虚拟显示器——
/// 这是串流进行中的可靠拓扑信号（Moonlight 视频走 UDP，TCP 控制连接不持续，
/// 端口检测会在串流中途失检）。
/// </summary>
internal static class DisplayTopology
{
    /// <summary>枚举当前活动显示器。</summary>
    public static List<ActiveMonitor> GetActiveMonitors()
    {
        var virtualNames = GetVirtualDeviceNames();
        var result = new List<ActiveMonitor>();

        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                szDevice = string.Empty,
            };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var isPrimary = (info.dwFlags & Win32Constants.MONITORINFOF_PRIMARY) != 0;
                var isVirtual = virtualNames.Contains(info.szDevice ?? string.Empty);
                result.Add(new ActiveMonitor(
                    info.rcMonitor, info.rcWork, info.szDevice ?? string.Empty, isPrimary, isVirtual));
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>是否存在处于活动状态的物理（非虚拟）显示器。</summary>
    public static bool HasPhysicalDisplay()
    {
        try
        {
            return GetActiveMonitors().Any(m => !m.IsVirtual);
        }
        catch
        {
            // 读取失败（如拓扑切换瞬间）按“有物理屏”处理：继续正常保护，绝不误停。
            return true;
        }
    }

    /// <summary>识别虚拟显示适配器（Virtual Display Driver / 各类 IddCx 间接显示驱动）。</summary>
    internal static HashSet<string> GetVirtualDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = new NativeMethods.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty,
        };

        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if (IsVirtualAdapter(device.DeviceString))
            {
                names.Add(device.DeviceName);
            }

            device = new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty,
            };
        }
        return names;
    }

    /// <summary>
    /// 枚举适配器及其显示路径的状态位（EnumDisplayDevices 两级）。
    /// 为什么需要它：实测物理屏形态下虚拟适配器 `\\.\DISPLAY6` 是 ACTIVE、其 Monitor0 是 ACTIVE|ATTACHED，
    /// 但它不在桌面布局里（EnumDisplayMonitors 看不到）。"路径在"与"能当屏幕用"必须分开判，
    /// 否则一键启用要么被永久拦住、要么在没有输出时停用物理屏。
    /// </summary>
    public static IReadOnlyList<DisplayPathInfo> GetDisplayPaths()
    {
        var result = new List<DisplayPathInfo>();

        var adapter = NewDevice();
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            var target = NewDevice();
            for (uint j = 0; NativeMethods.EnumDisplayDevices(adapter.DeviceName, j, ref target, 0); j++)
            {
                result.Add(new DisplayPathInfo(adapter.DeviceName, adapter.DeviceString, adapter.DeviceID,
                    adapter.StateFlags, target.StateFlags));
                target = NewDevice();
            }

            adapter = NewDevice();
        }

        return result;
    }

    /// <summary>适配器描述是否属于虚拟/间接显示方案（与 GetVirtualDeviceNames 用同一套线索）。</summary>
    /// <summary>
    /// 泛指"任何虚拟/间接显示适配器"，只用于串流检测（判断本地屏幕是否被接管）。
    /// 绝不能用来认领"Guardian 自己的虚拟屏" —— 第三方远程软件也在这里注册（新机实测：ToDesk 的
    /// Todesk Virtual Display Adapter 同样占着 ROOT/DISPLAY 实例），认领错了就会跳过装驱动。
    /// </summary>
    public static bool IsVirtualAdapter(string? description)
    {
        var text = (description ?? string.Empty).ToLowerInvariant();
        return text.Contains("virtual") || text.Contains("idd") || text.Contains("vdd") || text.Contains("indirect");
    }

    /// <summary>
    /// 只认我们自己安装的那一个驱动：硬件/实例标识里必须出现 MttVDD，
    /// 或适配器描述是厂商给定的 "Virtual Display Driver" / 含 VDD、MTT 字样。
    /// </summary>
    public static bool IsOwnVirtualAdapter(string? deviceId, string? description)
    {
        var id = (deviceId ?? string.Empty).ToLowerInvariant();
        if (id.Contains("mttvdd"))
        {
            return true;
        }

        var text = (description ?? string.Empty).ToLowerInvariant();
        return text.Contains("vdd") || text.Contains("mtt") || text == "virtual display driver";
    }

    /// <summary>我们自己那个驱动占用的适配器设备名（如 \.\DISPLAY6），用于把桌面里的屏幕精确归到它身上。</summary>
    public static HashSet<string> GetOwnVirtualAdapterNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapter = NewDevice();
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if (IsOwnVirtualAdapter(adapter.DeviceID, adapter.DeviceString))
            {
                names.Add(adapter.DeviceName);
            }

            adapter = NewDevice();
        }

        return names;
    }

    private static NativeMethods.DISPLAY_DEVICE NewDevice() => new()
    {
        cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
        DeviceName = string.Empty,
        DeviceString = string.Empty,
        DeviceID = string.Empty,
        DeviceKey = string.Empty,
    };

    /// <summary>一条显示路径：属于哪个适配器、适配器是什么、以及目标的状态位。</summary>
    /// <summary>
    /// 一条显示路径。StateFlags 用位判定，不要凭字面猜：
    /// 0x1 ACTIVE（在桌面里）、0x2 ATTACHED（物理连着）。被 Sunshine 停用的物理屏是
    /// "适配器存在、目标 ATTACHED 但适配器不带 ACTIVE"，正是紧急键要接回的对象。
    /// </summary>
    public sealed record DisplayPathInfo(
        string AdapterName, string AdapterDescription, string AdapterDeviceId, int AdapterStateFlags, int TargetStateFlags);

    public sealed record ActiveMonitor(
        NativeMethods.RECT Monitor, NativeMethods.RECT Work, string DeviceName, bool IsPrimary, bool IsVirtual);
}
