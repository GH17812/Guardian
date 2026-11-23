using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Guardian.Windows.Display;

/// <summary>
/// 物理显示器重新挂载（ChangeDisplaySettingsEx，与 Sunshine 显示切换同族 API）。
/// 背景：Sunshine 虚拟显示器会话（ensure_only_display）结束后，物理屏一旦熄灭，
/// SC_MONITORPOWER 开屏广播与鼠标微动均无法唤醒（本机实测：面板只在“重新挂载”时点亮，
/// 用户实测的唯一恢复办法是再开一次串流再退出——Sunshine 恢复拓扑时的挂载事件点亮面板）。
/// 因此本类复刻该机制：把物理显示器“摘下→等待→插回”，强制面板输出管线重新初始化。
/// 说明：本机 SetDisplayConfig（CCD）对任何输入都返回 ERROR_INVALID_PARAMETER（原因不明），
/// 而 ChangeDisplaySettingsEx 实测全部成功，故采用后者。
/// 安全设计：
/// 1. 挂载前检查串流状态，串流中绝不执行（会与 Sunshine 的拓扑切换打架）；
/// 2. 一旦摘下，无论发生什么都必须插回（插回失败走 displayswitch /extend 兜底 + 重试）。
/// </summary>
public static class DisplayReattacher
{
    private const int EnumCurrentSettings = -1;
    private const uint CdsUpdateRegistry = 0x1;

    /// <summary>
    /// 重新挂载全部物理显示器。abortIfStreaming 用于在挂载前确认没有串流。
    /// 返回“摘下并插回”是否成功。
    /// </summary>
    public static bool ReattachPhysicalDisplays(Func<bool> abortIfStreaming)
    {
        try
        {
            if (abortIfStreaming())
            {
                return false;
            }

            var physicalDevices = DisplayTopology.GetActiveMonitors()
                .Where(m => !m.IsVirtual)
                .Select(m => m.DeviceName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (physicalDevices.Count == 0)
            {
                // 桌面里一个物理屏都没有 = Sunshine 已把它们停用。
                // 此时"摘下→插回"没有任何可枚举的对象，直接返回 false ——
                // 这正是紧急键实测失败的原因（app-debug.log：仅剩虚拟屏活动 → 重新挂载物理屏失败）。
                // 换一条路：把适配器仍在、但已不在桌面里的物理显卡重新接回桌面。
                return AttachInactivePhysicalDisplays();
            }

            // 捕获每个物理屏的当前模式（插回时用于恢复原分辨率/位置）。
            var captured = new List<(string Device, DEVMODE Mode)>();
            foreach (var device in physicalDevices)
            {
                var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(device, EnumCurrentSettings, ref mode))
                {
                    captured.Add((device, mode));
                }
            }
            if (captured.Count == 0)
            {
                return false;
            }

            // 1. 摘下全部物理屏（更新注册表 + 应用）。
            foreach (var (device, _) in captured)
            {
                ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
            }
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            // 已摘下：无论串流是否开始，都必须插回（700ms 让面板断电复位）。
            Thread.Sleep(700);

            // 2. 插回（原模式）。
            foreach (var (device, mode) in captured)
            {
                var applyMode = mode;
                ChangeDisplaySettingsEx(device, ref applyMode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
            }
            var apply = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (apply != 0)
            {
                // 兜底：display switch 恢复注册表拓扑后再应用一次。
                TryDisplaySwitchExtend();
                Thread.Sleep(1000);
                apply = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            }
            return apply == 0;
        }
        catch
        {
            // 异常时确保拓扑回到注册表配置，绝不留显示器处于摘下状态。
            TryDisplaySwitchExtend();
            return false;
        }
    }

    /// <summary>
    /// 把"路径存在但不在桌面布局里"的虚拟屏接进桌面。与厂商工具 Connect This Device 同族：
    /// 对虚拟适配器取一个可用模式，用 ChangeDisplaySettingsEx + CDS_UPDATEREGISTRY 挂上去，再 apply NULL 生效。
    /// 成功判据是接完再看 EnumDisplayMonitors 里有没有虚拟屏，不靠 API 返回值。
    /// 为什么需要它：实测 Sunshine 只在客户端连接时才动拓扑，新机上客户端连不上就会永远卡在"没进桌面"。
    /// </summary>
    public static bool AttachVirtualDisplayToDesktop()
    {
        // 两层时序都要等（实测设备刚启用时两个阶段都可能缺席）：
        // 1. 适配器路径还没出现——GetOwnVirtualAdapterNames 为空，此时连枚举对象都没有；
        // 2. 路径出现了但驱动还没把模式列表发布出来——EnumDisplaySettings 取不到模式。
        // 统一重试窗口：每 1 秒轮询一次，任一步就绪即尝试挂载，挂载成功立即返回，
        // 窗口内两层都没就绪才判失败（此前路径未出现时零等待直接失败）。
        const int attachWindowMs = 25_000;
        var deadline = Environment.TickCount64 + attachWindowMs;
        while (Environment.TickCount64 < deadline)
        {
            var names = DisplayTopology.GetOwnVirtualAdapterNames();
            if (names.Count > 0)
            {
                var attached = 0;
                foreach (var device in names)
                {
                    var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                    if (!EnumDisplaySettings(device, 0, ref mode))
                    {
                        continue; // 模式列表还没发布：下一轮再试。
                    }

                    if (ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0)
                    {
                        attached++;
                    }
                }

                if (attached > 0)
                {
                    ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    Thread.Sleep(1500);
                    return DisplayTopology.GetActiveMonitors().Any(monitor => monitor.IsVirtual);
                }
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    /// <summary>
    /// 把"存在但不在桌面布局里"的物理显示适配器重新接回桌面。
    /// 用 ChangeDisplaySettingsEx 而不是 SetDisplayConfig：本机实测 SetDisplayConfig 对任何输入
    /// 都返回 ERROR_INVALID_PARAMETER，而 ChangeDisplaySettingsEx 全部成功（同文件开头已记录）。
    /// 成功判据不靠 API 返回值，而是接完再看一次桌面里有没有物理屏 —— 与紧急键的回执机制一致。
    /// </summary>
    private static bool AttachInactivePhysicalDisplays()
    {
        const int AdapterActive = 0x1;
        const int TargetAttached = 0x2;

        var candidates = DisplayTopology.GetDisplayPaths()
            .Where(path => !DisplayTopology.IsVirtualAdapter(path.AdapterDescription)
                        && (path.TargetStateFlags & TargetAttached) != 0
                        && (path.AdapterStateFlags & AdapterActive) == 0)
            .Select(path => path.AdapterName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        var attached = 0;
        foreach (var device in candidates)
        {
            // 停用中的适配器取不到 ENUM_CURRENT_SETTINGS，用枚举到的第一个模式接回。
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, 0, ref mode))
            {
                continue;
            }

            if (ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0)
            {
                attached++;
            }
        }

        if (attached == 0)
        {
            return false;
        }

        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Thread.Sleep(1500);

        return DisplayTopology.GetActiveMonitors().Any(monitor => !monitor.IsVirtual);
    }

    private static void TryDisplaySwitchExtend()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("displayswitch.exe", "/extend")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is not null)
            {
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // 兜底失败不影响主流程。
        }
    }

    // ---------------- 旧版显示 API P/Invoke（仅本类使用，集中于此） ----------------

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    /// <summary>DEVMODEA（156 字节），仅用于捕获/回放，字段语义无需解析。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
