using Guardian.Core.Ports;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Display;

/// <summary>
/// 显示隐私控制器（服务端/Session 0）：通过 WM_SYSCOMMAND/SC_MONITORPOWER 广播关闭物理显示器。
/// 说明：
/// - 服务运行于 Session 0，BroadcastSystemMessage 跨会话广播在部分环境不可靠；
///   实际关屏由客户端 DisplayPrivacyClient（交互会话内）执行，本控制器保留为双保险；
/// - 采用官方电源消息而非篡改显示拓扑，远程捕获（RDP/Sunshine/Parsec 等基于桌面合成/DXGI）通常不受影响；
/// - 物理输入或系统事件可能唤醒显示器，启用期间以定时器周期重申关闭；
/// - 是否适用于特定远程工具组合，需通过"显示隐私验证"流程在真实环境确认（见 CapabilityReport.DisplayPrivacyValidated）。
/// </summary>
public sealed class DisplayPrivacyController : IDisplayPrivacyController, IDisposable
{
    private static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private Timer? _keepOffTimer;
    private volatile bool _active;

    public bool IsActive => _active;

    public void Enable()
    {
        lock (_sync)
        {
            if (_active)
            {
                return;
            }
            BroadcastMonitorPower(Win32Constants.MONITOR_OFF);
            _keepOffTimer = new Timer(_ =>
            {
                try
                {
                    if (_active)
                    {
                        BroadcastMonitorPower(Win32Constants.MONITOR_OFF);
                    }
                }
                catch
                {
                    // 重申失败时保持静默；主路径由引擎恢复流程兜底。
                }
            }, null, ReapplyInterval, ReapplyInterval);
            _active = true;
        }
    }

    public void Disable()
    {
        lock (_sync)
        {
            _active = false;
            _keepOffTimer?.Dispose();
            _keepOffTimer = null;
            BroadcastMonitorPower(Win32Constants.MONITOR_ON);
        }
    }

    /// <summary>
    /// 使用 BroadcastSystemMessage(BSM_APPLICATIONS) 跨会话广播显示器电源消息：
    /// 服务运行在 Session 0，普通 PostMessage(HWND_BROADCAST) 无法到达交互式会话。
    /// </summary>
    private static void BroadcastMonitorPower(int state)
    {
        uint recipients = 0;
        NativeMethods.BroadcastSystemMessage(
            Win32Constants.BSM_APPLICATIONS | Win32Constants.BSF_POSTMESSAGE,
            ref recipients,
            Win32Constants.WM_SYSCOMMAND,
            (IntPtr)Win32Constants.SC_MONITORPOWER,
            (IntPtr)state);
    }

    public void Dispose() => Disable();
}
