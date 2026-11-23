using System.Runtime.InteropServices;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Power;

/// <summary>
/// 待机模式系统交互（对 Guardian.Windows 内部 NativeMethods 的公开封装）：
/// 1. 锁屏（退出待机的凭证校验——由 Windows 登录界面校验密码/PIN，本程序不接触凭据）；
/// 2. 离开模式（Away Mode）电源设置通知的注册与解析（判断系统是否处于离开模式）。
/// </summary>
public static class RestModeSupport
{
    // 电源广播常量（对客户端公开）。
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    /// <summary>GUID_SYSTEM_AWAYMODE：系统离开模式电源设置。</summary>
    private static readonly Guid AwayModeGuid = new("98a7f580-01f7-48aa-9c0f-44352c29e5c0");

    /// <summary>锁定工作站：屏幕点亮并显示登录界面（Windows 校验凭证）。</summary>
    public static void LockWorkstation()
    {
        try
        {
            NativeMethods.LockWorkStation();
        }
        catch
        {
            // 锁屏失败不阻塞唤醒主流程。
        }
    }

    /// <summary>
    /// 检查一组虚拟键当前是否全部处于按下状态（GetAsyncKeyState 内核级异步状态）。
    /// 紧急组合键轮询兜底通道使用：不依赖钩子/窗口，任何用户态按键都能查到。
    /// </summary>
    public static bool AreKeysPressed(params uint[] vks)
    {
        const short pressed = unchecked((short)0x8000);
        foreach (var vk in vks)
        {
            if ((NativeMethods.GetAsyncKeyState((int)vk) & pressed) == 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>紧急组合键当前是否按下：固定 Ctrl+Alt+Shift + 配置主键（与主界面所示组合一致）。</summary>
    public static bool IsEmergencyComboPressed(uint mainVk)
        => AreKeysPressed(
            (uint)Win32Constants.VK_CONTROL,
            (uint)Win32Constants.VK_MENU,
            (uint)Win32Constants.VK_SHIFT,
            mainVk);

    /// <summary>注册离开模式电源设置通知（hRecipient 为目标窗口句柄）。</summary>
    public static IntPtr RegisterAwayModeNotification(IntPtr hwnd)
    {
        try
        {
            var guid = AwayModeGuid; // 静态只读字段不能直接作 ref 实参，需先拷贝。
            return NativeMethods.RegisterPowerSettingNotification(hwnd, ref guid, 0);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void UnregisterNotification(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        try
        {
            NativeMethods.UnregisterPowerSettingNotification(handle);
        }
        catch
        {
            // 注销失败不影响主流程。
        }
    }

    /// <summary>解析 PBT_POWERSETTINGCHANGE 的 lParam，若为离开模式设置则输出是否进入离开模式。</summary>
    public static bool TryParseAwayModeSetting(IntPtr lParam, out bool engaged)
    {
        engaged = false;
        try
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting != AwayModeGuid || setting.DataLength < 1)
            {
                return false;
            }
            engaged = Marshal.ReadByte(lParam, Marshal.SizeOf<POWERBROADCAST_SETTING>()) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }
}
