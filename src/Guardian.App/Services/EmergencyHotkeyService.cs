using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Guardian.App.Services;

/// <summary>
/// 紧急恢复全局热键（Ctrl+Alt+Shift+主键）。
/// 必须在交互会话注册：服务端（Session 0）的低层钩子监听不到交互会话的按键，
/// 此前“紧急键无效”的根因即在此。客户端进程退出后热键随之失效，
/// 但关屏重申也随之停止（fail-open），显示器可自行恢复。
/// </summary>
public sealed class EmergencyHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4741; // "GA"
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly object _sync = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _vk = 0x7B; // 默认 F12
    private bool _registered;

    /// <summary>热键被按下（UI 线程回调）。</summary>
    public event Action? Pressed;

    /// <summary>注册状态变化（诊断日志用）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>挂接到指定窗口的消息循环并注册热键。</summary>
    public void Attach(Window window, int vk)
    {
        _vk = vk;
        _source = (HwndSource)PresentationSource.FromVisual(window);
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;
        Register();
    }

    /// <summary>更新主键（服务端设置加载/变更后调用）。</summary>
    public void UpdateKey(int vk)
    {
        lock (_sync)
        {
            if (vk == _vk)
            {
                return;
            }
            UnregisterCore();
            _vk = vk;
            RegisterCore();
        }
    }

    private void Register()
    {
        lock (_sync)
        {
            RegisterCore();
        }
    }

    private void RegisterCore()
    {
        if (_hwnd == IntPtr.Zero || _registered)
        {
            return;
        }
        // 修饰键固定为 Ctrl+Alt+Shift + 配置主键：与主界面所示组合完全一致，不做变体放宽。
        const uint modifiers = ModAlt | ModControl | ModShift;
        if (!RegisterHotKey(_hwnd, HotkeyId, modifiers | ModNoRepeat, (uint)_vk)
            && !RegisterHotKey(_hwnd, HotkeyId, modifiers, (uint)_vk))
        {
            // 注册失败（主键被其他程序占用）：热键不可用。
            StatusChanged?.Invoke(
                $"hotkey register FAILED vk=0x{_vk:X} err={Marshal.GetLastWin32Error()}");
            return;
        }
        _registered = true;
        StatusChanged?.Invoke($"hotkey registered Ctrl+Alt+Shift+vk=0x{_vk:X}");
    }

    private void UnregisterCore()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            StatusChanged?.Invoke("hotkey PRESSED");
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            UnregisterCore();
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
