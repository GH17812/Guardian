namespace Guardian.Windows.Interop;

/// <summary>
/// 紧急组合键（Ctrl+Alt+Shift+主键）的修饰键判定，供各低层钩子共用。
///
/// 为什么不能只问 GetAsyncKeyState：输入隔离/待机吞键期间，正是钩子把 Ctrl/Alt/Shift 的
/// 按下事件拦截掉的，异步按键表因此可能不反映它们——判定永远不成立，紧急键形同失效
/// （交接文档 20/21 轮“三通道全灭”与此一致）。低层钩子是每个物理键事件的必经点，
/// 由它亲眼记账，再与异步状态取并集，才是可靠依据。
///
/// 组合语义严格保持“三者齐全”，与主界面所示一致，不做任何放宽。
/// </summary>
internal sealed class EmergencyComboState
{
    private const int CtrlBit = 0b001;
    private const int AltBit = 0b010;
    private const int ShiftBit = 0b100;
    private const int AllBits = CtrlBit | AltBit | ShiftBit;

    private int _mask;

    /// <summary>记录钩子亲眼所见的修饰键按下/抬起（左右键归入同一组，非修饰键忽略）。</summary>
    public void Track(uint vk, bool isDown)
    {
        var bit = vk switch
        {
            0x11 or 0xA2 or 0xA3 => CtrlBit,  // VK_CONTROL / L / R
            0x12 or 0xA4 or 0xA5 => AltBit,   // VK_MENU / L / R
            0x10 or 0xA0 or 0xA1 => ShiftBit, // VK_SHIFT / L / R
            _ => 0,
        };
        if (bit == 0)
        {
            return;
        }
        var mask = _mask;
        _mask = isDown ? mask | bit : mask & ~bit;
    }

    /// <summary>主键按下时判定组合是否成立；evidence 为两个来源的读数，供日志定位分歧。</summary>
    public bool IsCompleteComboDown(out string evidence)
    {
        var tracked = _mask;
        var async = AsyncMask();
        evidence = $"tracked={Describe(tracked)} async={Describe(async)}";
        return ((tracked | async) & AllBits) == AllBits;
    }

    /// <summary>清零记账：钩子装载前后、以及组合命中后调用，防止漏收抬起造成粘滞误触发。</summary>
    public void Reset() => _mask = 0;

    private static int AsyncMask()
    {
        const short pressed = unchecked((short)0x8000);
        var mask = 0;
        if ((NativeMethods.GetAsyncKeyState(Win32Constants.VK_CONTROL) & pressed) != 0)
        {
            mask |= CtrlBit;
        }
        if ((NativeMethods.GetAsyncKeyState(Win32Constants.VK_MENU) & pressed) != 0)
        {
            mask |= AltBit;
        }
        if ((NativeMethods.GetAsyncKeyState(Win32Constants.VK_SHIFT) & pressed) != 0)
        {
            mask |= ShiftBit;
        }
        return mask;
    }

    private static string Describe(int mask)
        => $"C{Bit(mask, CtrlBit)}A{Bit(mask, AltBit)}S{Bit(mask, ShiftBit)}";

    private static string Bit(int mask, int bit) => (mask & bit) != 0 ? "1" : "0";
}
