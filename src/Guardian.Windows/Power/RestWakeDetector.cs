using System.ComponentModel;
using System.Runtime.InteropServices;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Power;

/// <summary>
/// 待机唤醒检测：WH_KEYBOARD_LL / WH_MOUSE_LL 低层钩子，检测物理（非注入）键鼠输入，
/// 用于“待机模式”下本地输入唤醒。远程串流输入带 INJECTED 标记，被过滤——手机端操作不会误唤醒待机。
/// 唤醒动作必须被吞掉：回调返回 true 时，本事件被拦截（返回 1），并且随后 1.2 秒内的
/// 全部物理输入一并吞掉——否则唤醒的那一下点击/按键会穿透到桌面应用
/// （例如点到 Guardian 界面的开关，把待机模式意外关掉）。
/// 通知节流：任意输入源 500ms 内只回调一次（鼠标移动事件高频）。
/// </summary>
public sealed class RestWakeDetector : IDisposable
{
    private static readonly TimeSpan NotifyThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>唤醒触发后吞掉物理输入的窗口时长（覆盖锁屏生效前的输入队列）。</summary>
    private static readonly TimeSpan SwallowWindow = TimeSpan.FromMilliseconds(1200);

    private readonly object _sync = new();
    private HookThread? _thread;

    /// <summary>检测到物理键鼠输入（后台钩子线程回调）。返回 true 表示吞掉该事件。订阅方须快速返回。</summary>
    public event Func<bool>? PhysicalInput;

    /// <summary>检测到紧急组合键（Ctrl+Alt+Shift+主键）。钩子线程回调，订阅方须自行派发。</summary>
    public event Action? Emergency;

    /// <summary>低层钩子安装失败（安全软件拦截/系统限制）。订阅方记录诊断。</summary>
    public event Action<Exception>? HookFailed;

    /// <summary>诊断日志（如用户实际按下的功能键 vkCode）。钩子线程回调。</summary>
    public event Action<string>? Diagnostics;

    private int _emergencyVk = 0x7B; // 默认 F12，服务端设置加载后更新。
    private long _lastInputTick;

    /// <summary>最近一次物理输入的时间戳（任意键鼠事件，含被吞掉的）。供“用户活动”探测。</summary>
    public long LastInputTick => Interlocked.Read(ref _lastInputTick);

    /// <summary>更新紧急组合键主键（服务端设置）。</summary>
    public void SetEmergencyVk(int vk) => Interlocked.Exchange(ref _emergencyVk, vk);

    /// <summary>当前紧急组合键主键（轮询兜底通道用）。</summary>
    public int EmergencyVk => Volatile.Read(ref _emergencyVk);

    public void Start()
    {
        lock (_sync)
        {
            if (_thread is not null)
            {
                return;
            }
            var thread = new HookThread(
                () => PhysicalInput?.Invoke() ?? false,
                () => Emergency?.Invoke(),
                () => Volatile.Read(ref _emergencyVk),
                () => Interlocked.Exchange(ref _lastInputTick, Environment.TickCount64),
                ex => HookFailed?.Invoke(ex),
                msg => Diagnostics?.Invoke(msg));
            thread.Start();
            _thread = thread;
        }
    }

    public void Stop()
    {
        HookThread? toStop;
        lock (_sync)
        {
            toStop = _thread;
            _thread = null;
        }
        toStop?.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>运行低层钩子的独立线程（需要消息循环）。</summary>
    private sealed class HookThread
    {
        private readonly Func<bool> _onPhysicalInput;
        private readonly Action _onEmergency;
        private readonly Func<int> _getEmergencyVk;
        private readonly Action _onInputSeen;
        private readonly Action<Exception> _onFailure;
        private readonly Action<string> _onDiagnostics;

        // 委托必须作为字段持有，防止被 GC 回收导致回调失效。
        private readonly NativeMethods.LowLevelKeyboardProc _kbProc;
        private readonly NativeMethods.LowLevelMouseProc _msProc;

        private Thread? _thread;
        private uint _threadId;
        private IntPtr _kbHook;
        private IntPtr _msHook;
        private long _lastNotifyTick;
        private long _swallowUntil;
        private long _lastEmergencyTick;
        private long _lastFKeyLogTick;

        // 修饰键记账见 EmergencyComboState：吞键期间本钩子拦截了 Ctrl/Alt/Shift 的按下事件，
        // 而实测中只依赖 GetAsyncKeyState 的判定从未命中（三通道全灭），故以钩子亲眼记账为主。
        private readonly EmergencyComboState _combo = new();

        public HookThread(
            Func<bool> onPhysicalInput,
            Action onEmergency,
            Func<int> getEmergencyVk,
            Action onInputSeen,
            Action<Exception> onFailure,
            Action<string> onDiagnostics)
        {
            _onPhysicalInput = onPhysicalInput;
            _onEmergency = onEmergency;
            _getEmergencyVk = getEmergencyVk;
            _onInputSeen = onInputSeen;
            _onFailure = onFailure;
            _onDiagnostics = onDiagnostics;
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
        }

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Guardian.RestWakeHook",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            var thread = _thread;
            if (thread is null || !thread.IsAlive)
            {
                return;
            }
            NativeMethods.PostThreadMessage(_threadId, Win32Constants.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!thread.Join(TimeSpan.FromSeconds(3)))
            {
                // 线程未能及时退出；进程退出时系统会移除钩子。
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

        /// <summary>
        /// 节流回调：500ms 内首次物理输入通知订阅方；返回是否吞掉该事件。
        /// 订阅方判定唤醒后，1.2 秒窗口内的全部物理输入一并吞掉（覆盖锁屏生效前的输入）。
        /// </summary>
        private bool NotifyThrottled()
        {
            var now = Environment.TickCount64;
            _onInputSeen(); // 记录用户活动时间戳（含吞键窗口内的输入——用户正在操作）。

            // 唤醒后的吞掉窗口：不论节流，全部拦截。
            if (now < Interlocked.Read(ref _swallowUntil))
            {
                return true;
            }

            if (now - Interlocked.Read(ref _lastNotifyTick) < (long)NotifyThrottle.TotalMilliseconds)
            {
                return false;
            }
            Interlocked.Exchange(ref _lastNotifyTick, now);

            if (_onPhysicalInput())
            {
                Interlocked.Exchange(ref _swallowUntil, now + (long)SwallowWindow.TotalMilliseconds);
                return true;
            }
            return false;
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var isDown = (uint)wParam == Win32Constants.WM_KEYDOWN || (uint)wParam == Win32Constants.WM_SYSKEYDOWN;
                    var isUp = (uint)wParam == Win32Constants.WM_KEYUP || (uint)wParam == Win32Constants.WM_SYSKEYUP;
                    if (isDown || isUp)
                    {
                        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                        if ((info.flags & Win32Constants.LLKHF_INJECTED) == 0)
                        {
                            // 先记账修饰键：所有物理键事件（含抬起）都要过这里，
                            // 下面命中分支会直接 return，不能让它跳过记账。
                            _combo.Track(info.vkCode, isDown);

                            // 紧急组合键：固定 Ctrl+Alt+Shift + 配置主键（与主界面所示一致），
                            // 任何状态下都识别——保护/待机吞掉输入时，它是本地唯一逃生通道
                            // （RegisterHotKey 可能被 LL 吞键拦截，这里自备识别）。
                            if (isDown && info.vkCode == (uint)_getEmergencyVk())
                            {
                                var hit = _combo.IsCompleteComboDown(out var evidence);
                                // 判定证据：用于区分“只按了主键”与“修饰键没被读到”——
                                // 二者在日志里此前无法分辨，是紧急键排障一直悬而不决的原因。
                                _onDiagnostics($"emergency probe vk=0x{info.vkCode:X} {evidence} hit={hit}");
                                if (hit)
                                {
                                    var now = Environment.TickCount64;
                                    // 防抖：2 秒内不重复触发（按键重复/多钩子并存时各触发一次）。
                                    if (now - Interlocked.Read(ref _lastEmergencyTick) > 2000)
                                    {
                                        Interlocked.Exchange(ref _lastEmergencyTick, now);
                                        _combo.Reset(); // 命中后清零，漏收抬起不会粘滞误触发。
                                        _onEmergency();
                                    }
                                    Interlocked.Exchange(ref _swallowUntil, Environment.TickCount64 + (long)SwallowWindow.TotalMilliseconds);
                                    return (IntPtr)1;
                                }
                            }
                            // 诊断：记录全部非修饰键按下（节流 1 次/秒），用于定位“用户实际按出的键”
                            // （Fn+F12 等笔记本功能键会改变 vkCode，导致紧急键识别失配）。
                            if (isDown && !IsModifierVk(info.vkCode))
                            {
                                var tick = Environment.TickCount64;
                                if (tick - Interlocked.Read(ref _lastFKeyLogTick) > 1000)
                                {
                                    Interlocked.Exchange(ref _lastFKeyLogTick, tick);
                                    _onDiagnostics($"key down vk=0x{info.vkCode:X}");
                                }
                            }
                            if (NotifyThrottled())
                            {
                                return (IntPtr)1; // 吞掉唤醒输入（按下与抬起都吞，避免卡键）。
                            }
                        }
                    }
                }
            }
            catch
            {
                // 钩子回调绝不允许向外抛异常。
            }
            return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        /// <summary>修饰键（Ctrl/Alt/Shift/Win 及左右变体）不计入诊断日志。</summary>
        private static bool IsModifierVk(uint vk)
        {
            return (vk >= 0x10 && vk <= 0x12)
                || (vk >= 0x5B && vk <= 0x5C)
                || (vk >= 0xA0 && vk <= 0xA5)
                || vk == 0x13 || vk == 0x14 || vk == 0x15 || vk == 0x90 || vk == 0x91;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if ((info.flags & Win32Constants.LLMHF_INJECTED) == 0 && NotifyThrottled())
                    {
                        return (IntPtr)1; // 吞掉唤醒输入（点击按下与抬起都吞）。
                    }
                }
            }
            catch
            {
                // 同上，绝不外抛。
            }
            return NativeMethods.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }
    }
}
