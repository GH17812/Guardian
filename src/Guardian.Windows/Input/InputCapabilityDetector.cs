using System.Runtime.InteropServices;
using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Input;

/// <summary>输入设备能力检测（基于 Raw Input 设备枚举）。</summary>
public static class InputCapabilityDetector
{
    public static List<InputDeviceCapability> Detect()
    {
        var result = new List<InputDeviceCapability>();
        try
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();

            // 第一次调用返回所需缓冲区大小。
            NativeMethods.GetRawInputDeviceList(null, ref count, size);
            if (count == 0)
            {
                return result;
            }

            var list = new NativeMethods.RAWINPUTDEVICELIST[count];
            if (NativeMethods.GetRawInputDeviceList(list, ref count, size) == uint.MaxValue)
            {
                return result;
            }

            var counts = new Dictionary<InputDeviceType, int>();
            for (int i = 0; i < count; i++)
            {
                uint cbSize = (uint)Marshal.SizeOf<NativeMethods.RID_DEVICE_INFO>();
                var info = new NativeMethods.RID_DEVICE_INFO { cbSize = cbSize };
                if (NativeMethods.GetRawInputDeviceInfo(list[i].hDevice, Win32Constants.RIDI_DEVICEINFO, ref info, ref cbSize) == cbSize)
                {
                    var type = Map(info);
                    counts[type] = counts.GetValueOrDefault(type) + 1;
                }
            }

            foreach (var kv in counts.OrderBy(kv => kv.Key))
            {
                result.Add(new InputDeviceCapability { DeviceType = kv.Key, Count = kv.Value });
            }
        }
        catch
        {
            // 能力检测失败时返回空列表，由调用方标记为未完成。
        }
        return result;
    }

    private static InputDeviceType Map(NativeMethods.RID_DEVICE_INFO info)
    {
        switch (info.dwType)
        {
            case Win32Constants.RIM_TYPEMOUSE:
                return InputDeviceType.Mouse;
            case Win32Constants.RIM_TYPEKEYBOARD:
                return InputDeviceType.Keyboard;
            case Win32Constants.RIM_TYPEHID:
                // HID Digitizer 页（0x0D）：0x04 触摸屏，0x05 手写笔，0x02 触控板。
                if (info.Data.Hid.usUsagePage == 0x0D)
                {
                    return info.Data.Hid.usUsage switch
                    {
                        0x04 => InputDeviceType.TouchScreen,
                        0x05 => InputDeviceType.Pen,
                        0x02 => InputDeviceType.TouchPad,
                        _ => InputDeviceType.OtherHid,
                    };
                }
                // 通用桌面页（0x01）：0x04 游戏杆，0x05 手柄。
                if (info.Data.Hid.usUsagePage == 0x01 && info.Data.Hid.usUsage is 0x04 or 0x05)
                {
                    return InputDeviceType.GamePad;
                }
                return InputDeviceType.OtherHid;
            default:
                return InputDeviceType.OtherHid;
        }
    }
}
