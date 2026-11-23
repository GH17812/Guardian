using System.ComponentModel;
using System.Runtime.InteropServices;
using Guardian.Core.Ports;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Input;

/// <summary>
/// 实验性输入隔离控制器（Phase 1）。
/// 实现原理：WH_KEYBOARD_LL / WH_MOUSE_LL 低层钩子，拦截不带 INJECTED 标记（物理来源）的键鼠事件；
/// 带 INJECTED 标记（远程注入）的事件放行。
/// 已知限制（必须在 UI 中向用户明示）：
/// 1. 通过 RDP 输入栈注入的远程输入可能不带 INJECTED 标记，存在被误拦截的风险；
/// 2. 钩子回调超时可能被系统静默摘除（LowLevelHooksTimeout）；
/// 3. 安全桌面（Ctrl+Alt+Del/UAC）上的输入不受影响，SAS 始终可用。
/// 紧急恢复：钩子内部检测 Ctrl+Alt+Shift+主键 组合并回调引擎，恢复路径不依赖被隔离输入之外的通道。
/// </summary>
public sealed class InputIsolationController : IInputIsolationController, IDisposable
{
    private readonly object _sync = new();
    private HookThread? _hookThread;
    private volatile bool _active;

    public bool IsSupported => true; // 低层钩子全 Windows 10/11 可用；能力细节见能力报告。

    public bool IsActive => _active;

    /// <summary>诊断回调（可选）：输出紧急组合键判定证据，用于排查“按了没反应”。</summary>
    public Action<string>? Diagnostics { get; set; }

    public void Enable(Action onEmergency, string emergencyHotkeyKey)
    {
        lock (_sync)
        {
            if (_active)
            {
                return;
            }

            var thread = new HookThread(
                HotkeyMap.Resolve(emergencyHotkeyKey),
                () => Task.Run(onEmergency), // 钩子回调内不得阻塞
                ex => throw new InvalidOperationException($"输入隔离钩子安装失败：{ex.Message}", ex),
                Diagnostics);

            thread.Start();
            _hookThread = thread;
            _active = true;
        }
    }

    public void Disable()
    {
        HookThread? toStop;
        lock (_sync)
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            toStop = _hookThread;
            _hookThread = null;
        }

        toStop?.Stop();
    }

    public void Dispose() => Disable();

    /// <summary>运行低层钩子的独立线程（需要消息循环）。</summary>
    private sealed class HookThread
    {
        private readonly int _emergencyVk;
        private readonly Action _onEmergency;
        private readonly Action<Exception> _onFailure;
        private readonly Action<string>? _diagnostics;

        // 委托必须作为字段持有，防止被 GC 回收导致回调失效。
        private readonly NativeMethods.LowLevelKeyboardProc _kbProc;
        private readonly NativeMethods.LowLevelMouseProc _msProc;

        private Thread? _thread;
        private uint _threadId;
        private IntPtr _kbHook;
        private IntPtr _msHook;
        private volatile bool _comboFired;

        // 修饰键记账见 EmergencyComboState：隔离期间本钩子会吞掉 Ctrl/Alt/Shift 的按下事件，
        // 而实测中只依赖 GetAsyncKeyState 的判定从未命中（三通道全灭），故以钩子亲眼记账为主。
        private readonly EmergencyComboState _combo = new();

        public HookThread(
            int emergencyVk,
            Action onEmergency,
            Action<Exception> onFailure,
            Action<string>? diagnostics)
        {
            _emergencyVk = emergencyVk;
            _onEmergency = onEmergency;
            _onFailure = onFailure;
            _diagnostics = diagnostics;
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
        }

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Guardian.InputHook",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            var t = _thread;
            if (t is null || !t.IsAlive)
            {
                return;
            }
            NativeMethods.PostThreadMessage(_threadId, Win32Constants.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!t.Join(TimeSpan.FromSeconds(3)))
            {
                // 线程未能及时退出；进程退出时系统会移除钩子，且物理输入不再被拦截的进程处理。
            }
        }

        private void Run()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _combo.Reset(); // 装钩子前后可能漏收抬起，清零避免修饰键粘滞。
            _kbHook = NativeMethods.SetWindowsHookEx(Win32Constants.WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);
            _msHook = NativeMethods.SetWindowsHookEx(Win32Constants.WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);

            if (_kbHook == IntPtr.Zero)
            {
                _onFailure(new Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }

            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // 消息泵：低层钩子依赖线程消息循环。
            }

            if (_msHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_msHook);
            }
            NativeMethods.UnhookWindowsHookEx(_kbHook);
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    var isDown = (uint)wParam == Win32Constants.WM_KEYDOWN || (uint)wParam == Win32Constants.WM_SYSKEYDOWN;
                    var isUp = (uint)wParam == Win32Constants.WM_KEYUP || (uint)wParam == Win32Constants.WM_SYSKEYUP;

                    // 记账必须在任何吞键分支之前：修饰键按下事件在下面会被整体拦截。
                    _combo.Track(info.vkCode, isDown);

                    if (isDown && info.vkCode == _emergencyVk)
                    {
                        if (_combo.IsCompleteComboDown(out var evidence))
                        {
                            Diag($"[isolation] emergency COMBO HIT {evidence}");
                            _combo.Reset(); // 命中后清零，漏收抬起不会粘滞误触发。
                            if (!_comboFired)
                            {
                                _comboFired = true;
                                _onEmergency();
                            }
                            return (IntPtr)1; // 吞掉紧急键本身
                        }
                        // 主键到了、组合不成立：记下两个来源的读数，
                        // 用于区分“用户没按满三个修饰键”与“修饰键没被读到”。
                        Diag($"[isolation] emergency miss {evidence}");
                    }
                    else if (isUp && info.vkCode == _emergencyVk)
                    {
                        _comboFired = false; // 紧急键抬起后允许再次触发
                    }

                    // 物理输入（无 INJECTED 标记）→ 拦截；远程注入 → 放行。
                    if ((info.flags & Win32Constants.LLKHF_INJECTED) == 0)
                    {
                        return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // 钩子回调绝不允许向外抛异常。
            }

            return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if ((info.flags & Win32Constants.LLMHF_INJECTED) == 0)
                    {
                        return (IntPtr)1; // 物理鼠标输入 → 拦截
                    }
                }
            }
            catch
            {
                // 同上，绝不外抛。
            }

            return NativeMethods.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 诊断落盘（异步）：DiagLog 是同步文件写入，绝不能发生在钩子回调里——
        /// 钩子超时会连同所有键鼠拦截一起失效（fail-open 变成不可控）。
        /// </summary>
        private void Diag(string message)
        {
            var sink = _diagnostics;
            if (sink is null)
            {
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    sink(message);
                }
                catch
                {
                    // 诊断失败不影响钩子。
                }
            });
        }
    }
}
