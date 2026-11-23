namespace Guardian.Windows.Display;

/// <summary>
/// 交互会话看到的显示器事实。只有计数与布尔，不含分辨率、设备名，更不含屏幕内容。
/// VirtualDisplayEnumerated = 虚拟屏已是桌面布局里的一块输出（真能当屏幕用）；
/// VirtualDisplayPathActive = 虚拟适配器有 ACTIVE|ATTACHED 显示路径（可被激活，但可能还不在桌面里）。
/// 两者必须分开：实测物理屏形态下路径常在、桌面里没有它。
/// </summary>
public sealed record SessionDisplayFacts(
    bool VirtualDisplayEnumerated,
    bool VirtualDisplayPathActive,
    bool PhysicalDisplayActive,
    int ActiveMonitorCount);

/// <summary>
/// 供客户端上报用的显示拓扑读数。
/// 之所以要在 Windows 层开这个口子：DisplayTopology 是 internal，且 Session 0 读不到屏幕，
/// "虚拟屏是仅有路径、还是已经能用"只有运行在用户会话里的进程能回答。
/// </summary>
public static class SessionDisplayFactsReader
{
    /// <summary>DISPLAY_DEVICE_ACTIVE | DISPLAY_DEVICE_ATTACHED。</summary>
    private const int ActiveAndAttached = 0x1 | 0x2;

    public static SessionDisplayFacts Read()
    {
        var monitors = DisplayTopology.GetActiveMonitors();
        var ownNames = DisplayTopology.GetOwnVirtualAdapterNames();

        // 只认自己装的那个驱动：第三方远程软件（ToDesk 等）也注册虚拟显示适配器，
        // 把它们当成本功能的目标会让互锁与设备认领双双出错。
        var ours = DisplayTopology.GetDisplayPaths()
            .Where(path => ownNames.Contains(path.AdapterName)
                || DisplayTopology.IsOwnVirtualAdapter(path.AdapterDeviceId, path.AdapterDescription))
            .ToList();

        var pathActive = ours.Any(path => (path.TargetStateFlags & ActiveAndAttached) == ActiveAndAttached);
        var inDesktop = monitors.Any(monitor => ownNames.Contains(monitor.DeviceName));

        return new SessionDisplayFacts(
            inDesktop,
            pathActive,
            monitors.Any(monitor => !monitor.IsVirtual),
            monitors.Count);
    }
}
