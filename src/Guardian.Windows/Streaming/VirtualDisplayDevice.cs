using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Guardian.Windows.Streaming;

/// <summary>虚拟显示根设备的实测状态（只来自 SetupAPI/CfgMgr32，不解析任何命令行文本）。</summary>
public sealed record VirtualDisplayDeviceState
{
    public bool Present { get; init; }
    public bool Enabled { get; init; }
    public bool DriverStarted { get; init; }
    public string InstanceId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint ConfigFlags { get; init; }
    public uint Problem { get; init; }
    public string Evidence { get; init; } = string.Empty;

    public static VirtualDisplayDeviceState NotFound(string evidence) => new() { Present = false, Evidence = evidence };
}

/// <summary>
/// 虚拟显示根设备（硬件 ID <c>Root\MttVDD</c>，实例 <c>ROOT\DISPLAY\0000</c>）的探测、启停与创建。
/// 本机实测属性：Class=Display、ClassGuid={4D36E968-E325-11CE-BFC1-08002BE10318}、Service=WUDFRd、驱动 11.30.4.434。
/// 启停用 DIF_PROPERTYCHANGE（设备管理器的同源路径），创建用 DIF_REGISTERDEVICE（等价厂商安装程序的 “Add device”）；
/// 一键卸载只禁用设备、不删驱动包——留着才能再一键启用，且避免触发重启。
/// </summary>
public static class VirtualDisplayDevice
{
    public const string HardwareId = @"Root\MttVDD";

    /// <summary>
    /// 禁用态问题码，取自本机 SDK 的 shared/cfg.h：CM_PROB_DISABLED=0x16、CM_PROB_DISABLED_SERVICE=0x20
    /// （后者是驱动服务被禁用导致设备不可用，同样属于禁用）。本机 WMI 实测两台禁用设备码均为 22。
    /// </summary>
    public const uint ProbDisabled = 0x00000016;

    private const uint ProbDisabledService = 0x00000020;

    /// <summary>没有可用驱动：cfg.h 的 CM_PROB_FAILED_INSTALL = 0x1C（设备管理器“代码 28”）。</summary>
    public const uint ProbNoDriver = 0x0000001C;

    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    /// <summary>按硬件 ID 定位 root 枚举的显示设备；找不到时 Present=false，并在 Evidence 里列出实际看到的设备。</summary>
    public static VirtualDisplayDeviceState Probe(string hardwareId = HardwareId)
    {
        var seen = new List<string>();
        foreach (var (instance, hardware, description) in EnumerateRootDisplays(seen))
        {
            if (Matches(hardware, instance, hardwareId))
            {
                return ReadStatus(instance, description);
            }
        }

        return VirtualDisplayDeviceState.NotFound(
            $"未找到硬件 ID 为 {hardwareId} 的设备。ROOT 枚举下看到的显示设备：{(seen.Count == 0 ? "无" : string.Join("；", seen))}");
    }

    /// <summary>启用或禁用设备。以轮询后的实测状态判定成功，不信 API 返回码的字面意思。</summary>
    public static VirtualDisplayDeviceState TrySetEnabled(bool enabled, string hardwareId = HardwareId)
    {
        var current = Probe(hardwareId);
        if (!current.Present)
        {
            return current;
        }

        if (current.Enabled == enabled)
        {
            return current with { Evidence = $"设备已是{(enabled ? "启用" : "禁用")}状态，未做改动：{current.InstanceId}。" };
        }

        var handle = OpenRootDisplays();
        if (handle.IsInvalid)
        {
            return VirtualDisplayDeviceState.NotFound($"枚举显示设备失败：{Win32Error()}");
        }

        using (handle)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(handle, index, ref data); index++)
            {
                var instance = ReadInstanceId(handle, ref data);
                if (!Matches(ReadMultiSzProperty(handle, ref data, SpdrpHardwareId), instance, hardwareId))
                {
                    continue;
                }

                var change = new DICS_PARAM
                {
                    Header = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = DifPropertychange,
                    },
                    StateChange = enabled ? DicsEnable : DicsDisable,
                    Scope = DicsFlagGlobal,
                    HwProfile = 0,
                };

                if (!SetupDiSetClassInstallParams(handle, ref data, ref change, Marshal.SizeOf<DICS_PARAM>()))
                {
                    return VirtualDisplayDeviceState.NotFound($"提交变更参数失败：{Win32Error()}");
                }

                if (!SetupDiCallClassInstaller(DifPropertychange, handle, ref data))
                {
                    return VirtualDisplayDeviceState.NotFound($"{(enabled ? "启用" : "禁用")}失败：{Win32Error()}");
                }

                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var after = Probe(hardwareId);
                    if (after.Present && after.Enabled == enabled)
                    {
                        return after with { Evidence = $"{after.InstanceId} 已{(enabled ? "启用" : "禁用")}（驱动包保留，未卸载）。" };
                    }

                    Thread.Sleep(250);
                }

                var stalled = Probe(hardwareId);
                return stalled with
                {
                    Evidence = $"变更请求已提交但状态未到位：期望{(enabled ? "启用" : "禁用")}，"
                        + $"实测状态位 0x{stalled.ConfigFlags:X}、问题码 {stalled.Problem}。",
                };
            }
        }

        return VirtualDisplayDeviceState.NotFound($"枚举中找不到该设备：{current.InstanceId}");
    }

    /// <summary>
    /// 创建 root 枚举的虚拟显示设备。驱动包必须已经在系统里（<paramref name="infPath"/> 已加入驱动存储），
    /// 实例编号沿用厂商做法（ROOT\DISPLAY\0000），已有实例时顺延，绝不复用已存在的 ID。
    /// </summary>
    public static VirtualDisplayDeviceState TryCreate(string infPath, string hardwareId = HardwareId)
    {
        var existing = Probe(hardwareId);
        if (existing.Present)
        {
            return existing with { Evidence = $"设备已存在：{existing.InstanceId}，无需创建。" };
        }

        if (!File.Exists(infPath))
        {
            return VirtualDisplayDeviceState.NotFound($"驱动 INF 不存在，无法创建设备：{infPath}");
        }

        var instanceId = NextInstanceId();
        var classGuid = DisplayClassGuid;
        var handle = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return VirtualDisplayDeviceState.NotFound($"创建设备信息集失败：{Win32Error()}");
        }

        using (handle)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(handle, instanceId, ref classGuid, null, IntPtr.Zero, 0, ref data))
            {
                return VirtualDisplayDeviceState.NotFound($"登记设备 {instanceId} 失败：{Win32Error()}");
            }

            var hardwareIdBytes = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(handle, ref data, SpdrpHardwareId, hardwareIdBytes, (uint)hardwareIdBytes.Length))
            {
                return VirtualDisplayDeviceState.NotFound($"写入硬件 ID {hardwareId} 失败：{Win32Error()}");
            }

            if (!SetupDiCallClassInstaller(DifRegisterDevice, handle, ref data))
            {
                return VirtualDisplayDeviceState.NotFound($"创建设备 {instanceId} 失败：{Win32Error()}");
            }

            // 登记后必须把硬件 ID 读回来核对：写空值时 API 照样返回 TRUE，
            // 而空硬件 ID 的设备永远匹配不到驱动，只会变成孤儿设备（新机实测踩过两次）。
            var written = ReadHardwareIds(instanceId);
            if (!written.Any(id => id.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)))
            {
                return VirtualDisplayDeviceState.NotFound(
                    $"设备 {instanceId} 已登记，但硬件 ID 读回为 [{string.Join("|", written)}]，"
                    + $"与期望 {hardwareId} 不符：驱动不会绑定到它，请删除该设备后重试");
            }

            // 只靠 pnputil /install + PnP 自动匹配，实测出现过 Service 仍为空、驱动不绑定的情况；
            // 显式调用 UpdateDriverForPlugAndPlayDevices 才把驱动装到这个新设备上。
            var bound = UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath,
                InstallFlagForce | InstallFlagNonInteractive, out var rebootRequired);
            var bindNote = bound
                ? (rebootRequired ? "显式绑定成功，需重启才完全生效" : "显式绑定成功")
                : $"显式绑定失败：{Win32Error()}";

            // PnP 装驱动是异步的，等到问题码不再表示“没有驱动”为止。
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var created = Probe(hardwareId);
                if (created.Present && created.Problem != ProbNoDriver)
                {
                    return created with
                    {
                        Evidence = $"已创建 {created.InstanceId}（硬件 ID {string.Join("|", written)}），"
                            + $"{bindNote}，驱动取自 {Path.GetFileName(infPath)}",
                    };
                }

                Thread.Sleep(500);
            }

            var waiting = Probe(hardwareId);
            return waiting.Present
                ? waiting with
                {
                    Evidence = $"{waiting.InstanceId} 已创建，但驱动仍未加载（问题码 {waiting.Problem}）；"
                        + $"需确认 {Path.GetFileName(infPath)} 已加入驱动存储。",
                }
                : VirtualDisplayDeviceState.NotFound($"设备登记后未能枚举到：{instanceId}");
        }
    }

    /// <summary>
    /// 只校验启停参数结构（调 SetupDiSetClassInstallParams，不调类安装器）：
    /// 不执行任何变更、不需要管理员权限，因此可以放进普通测试。
    /// 它正是实测中报"参数错误"的那一步——DIF 码或结构布局写错，这里立刻失败。
    /// </summary>
    public static string TryValidateEnableParams(string hardwareId = HardwareId)
    {
        var current = Probe(hardwareId);
        if (!current.Present)
        {
            return current.Evidence;
        }

        var handle = OpenRootDisplays();
        if (handle.IsInvalid)
        {
            return $"枚举显示设备失败：{Win32Error()}";
        }

        using (handle)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(handle, index, ref data); index++)
            {
                var instance = ReadInstanceId(handle, ref data);
                if (!Matches(ReadMultiSzProperty(handle, ref data, SpdrpHardwareId), instance, hardwareId))
                {
                    continue;
                }

                var change = new DICS_PARAM
                {
                    Header = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = DifPropertychange,
                    },
                    StateChange = DicsEnable,
                    Scope = DicsFlagGlobal,
                    HwProfile = 0,
                };

                return SetupDiSetClassInstallParams(handle, ref data, ref change, Marshal.SizeOf<DICS_PARAM>())
                    ? $"变更参数结构被接受：DIF_PROPERTYCHANGE=0x{DifPropertychange:X} 大小 {Marshal.SizeOf<DICS_PARAM>()} 字节。"
                    : $"变更参数被拒绝：{Win32Error()}";
            }
        }

        return $"未找到设备：{hardwareId}";
    }

    private static VirtualDisplayDeviceState ReadStatus(string instance, string description)
    {
        var hr = CM_Locate_DevNode(out var devNode, instance, 0);
        if (hr != 0)
        {
            return VirtualDisplayDeviceState.NotFound($"定位设备节点失败（错误 {hr}）：{instance}");
        }

        if (CM_Get_DevNode_Status(out var flags, out var problem, devNode, 0) != 0)
        {
            return VirtualDisplayDeviceState.NotFound($"读取设备状态失败：{instance}");
        }

        // 只看问题码 22（本机实测：两台禁用设备 WMI ConfigManagerErrorCode 均为 22）。
        // 状态位不解读——CONFIGFLAG_* 各值的含义在本机无法取证（Enum 下没有 Control 子键），
        // 猜错会把正常设备读成“已禁用”，让互锁凭空拦住一键启用；原始值只作为取证文字保留。
        var disabled = problem == ProbDisabled || problem == ProbDisabledService;
        return new VirtualDisplayDeviceState
        {
            Present = true,
            Enabled = !disabled,
            DriverStarted = problem == 0,
            InstanceId = instance,
            Description = description,
            ConfigFlags = flags,
            Problem = problem,
            Evidence = $"{instance} 存在，{(disabled ? "已禁用" : "未禁用")}"
                + (problem == 0 ? string.Empty : $"，问题码 {problem}")
                + $"，状态位 0x{flags:X}。",
        };
    }

    /// <summary>枚举 ROOT 枚举器下的显示设备；<paramref name="seen"/> 用于失败时如实报告看到了什么。</summary>
    private static IEnumerable<(string Instance, string[] Hardware, string Description)> EnumerateRootDisplays(List<string> seen)
    {
        var handle = OpenRootDisplays();
        if (handle.IsInvalid)
        {
            yield break;
        }

        using (handle)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(handle, index, ref data); index++)
            {
                var instance = ReadInstanceId(handle, ref data);
                var hardware = ReadMultiSzProperty(handle, ref data, SpdrpHardwareId);
                var description = ReadMultiSzProperty(handle, ref data, SpdrpDeviceDesc);
                seen.Add($"{instance}[{string.Join("|", hardware)}]");
                yield return (instance, hardware, description.Length > 0 ? description[0] : string.Empty);
            }
        }
    }

    private static string NextInstanceId()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, _, _) in EnumerateRootDisplays(new List<string>()))
        {
            taken.Add(instance);
        }

        for (var slot = 0; slot < 1000; slot++)
        {
            var candidate = $@"ROOT\DISPLAY\{slot:D4}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("ROOT\\DISPLAY 的实例编号已用尽。");
    }

    /// <summary>
    /// 只按硬件 ID 匹配，绝不用实例 ID 前缀兜底。
    /// 全新机实测教训：ToDesk 的虚拟显示驱动就占着 ROOT/DISPLAY 下的一个实例编号，
    /// 之前那条"以 ROOT\DISPLAY\ 开头就算"的兜底把别人家的设备认成了我们的，
    /// 于是"根设备已存在"→ 跳过装驱动、跳过建设备 → 后面永远造不出虚拟屏，
    /// 而且每一步看起来都"成功"了。读不到硬件 ID 就报未找到，不要猜。
    /// </summary>
    private static bool Matches(string[] hardwareIds, string instanceId, string hardwareId)
        => hardwareIds.Any(id => id.Equals(hardwareId, StringComparison.OrdinalIgnoreCase));

    private static string ReadInstanceId(SafeHandle handle, ref SP_DEVINFO_DATA data)
    {
        if (!SetupDiGetDeviceInstanceId(handle, ref data, null, 0, out var needed) && needed == 0)
        {
            return string.Empty;
        }

        var buffer = new char[needed + 1];
        if (!SetupDiGetDeviceInstanceId(handle, ref data, buffer, (uint)buffer.Length, out var written))
        {
            return string.Empty;
        }

        return new string(buffer, 0, (int)written).TrimEnd('\0');
    }

    private static string[] ReadMultiSzProperty(SafeHandle handle, ref SP_DEVINFO_DATA data, int property)
    {
        // 第一次调用只是问长度：按 Win32 约定它必然返回 FALSE 并填上所需字节数，这不是失败。
        // 早先把它当失败直接返回空，导致硬件 ID 永远读不到 —— 于是只能靠实例 ID 前缀兜底，
        // 而那条兜底在新机上把 ToDesk 的设备错认成了我们自己的驱动。
        _ = SetupDiGetDeviceRegistryProperty(handle, ref data, property, out _, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return Array.Empty<string>();
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(handle, ref data, property, out _, buffer, needed, out var written) || written == 0)
            {
                return Array.Empty<string>();
            }

            // 按字节取回再自行解码：REG_MULTI_SZ 是 UTF-16，长度单位一旦理解错就会越界读。
            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, (int)written);

            return Encoding.Unicode.GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>按实例 ID 读回硬件 ID（写入后的自证，不靠 API 返回值）。</summary>
    private static string[] ReadHardwareIds(string instanceId)
    {
        var handle = OpenRootDisplays();
        if (handle.IsInvalid)
        {
            return Array.Empty<string>();
        }

        using (handle)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(handle, index, ref data); index++)
            {
                if (ReadInstanceId(handle, ref data).Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return ReadMultiSzProperty(handle, ref data, SpdrpHardwareId);
                }
            }
        }

        return Array.Empty<string>();
    }

    private static string Win32Error() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    /// <summary>SetupAPI 的 GUID 参数是指针语义，必须先落成局部变量再按 ref 传。</summary>
    private static SafeDevInfoHandle OpenRootDisplays()
    {
        var classGuid = DisplayClassGuid;
        return SetupDiGetClassDevs(ref classGuid, "ROOT", IntPtr.Zero, DigcfPresent);
    }

    // ---------------- SetupAPI / CfgMgr32（仅本类使用，集中于此） ----------------

    private const uint DigcfPresent = 0x00000002;

    // DIF 码取自本机 Windows SDK（10.0.26100）的 um/SetupAPI.h，不要凭印象改：
    // 早先按记忆写成 0x0B/0x05，SetupDiSetClassInstallParams 直接回 ERROR_INVALID_PARAMETER，
    // 实测表现为"一键恢复"里禁用虚拟设备这一步失败。
    private const uint DifPropertychange = 0x00000012;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint DicsEnable = 1;
    private const uint DicsDisable = 2;
    private const uint DicsFlagGlobal = 1;
    /// <summary>INSTALLFLAG_FORCE / INSTALLFLAG_NONINTERACTIVE（newdev.h，10.0.26100）。</summary>
    private const uint InstallFlagForce = 0x00000001;

    private const uint InstallFlagNonInteractive = 0x00000004;
    private const int SpdrpDeviceDesc = 0x00000000;
    private const int SpdrpHardwareId = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public int cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DICS_PARAM
    {
        public SP_CLASSINSTALL_HEADER Header;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    private sealed class SafeDevInfoHandle : SafeHandle
    {
        public SafeDevInfoHandle() : base(new IntPtr(-1), ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == new IntPtr(-1);

        // 句柄本就无效时不能再调 Destroy：ReleaseHandle 返回 false 会让运行时抛异常，
        // 而"SetupAPI 打不开"正是我们要如实上报的场景，不该变成进程崩溃。
        protected override bool ReleaseHandle() => handle == new IntPtr(-1) || SetupDiDestroyDeviceInfoList(handle);
    }

    // SetupAPI 的字符串接口有 A/W 两套，DllImport 默认绑 A 版：会把 REG_MULTI_SZ 返回成 ANSI 字节，
    // 按 UTF-16 解码就成了乱码（实测读到 "潒瑯䵜瑴䑖D" 这种"Root\MttVDD"错位），
    // 而写入硬件 ID 时我们给的是 UTF-16 字节 —— 两边都会错。所以全部显式绑 W 版。
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern SafeDevInfoHandle SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern SafeDevInfoHandle SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(SafeHandle deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    private static extern bool SetupDiGetDeviceInstanceId(SafeHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, char[]? deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(SafeHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property, out uint regType, IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    // 官方签名是 5 个参数（SetupAPI.h，10.0.26100）：少写 PropertyBufferSize 时调用可能返回 TRUE
    // 却写入空的 REG_MULTI_SZ —— 全新机实测因此每点一次就留下一个没有硬件 ID 的孤儿设备。
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryProperty(SafeHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(SafeHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DICS_PARAM classInstallParams, int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, SafeHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    private static extern bool SetupDiCreateDeviceInfo(SafeHandle deviceInfoSet, string deviceName, ref Guid classGuid, string? deviceDescription, IntPtr hwnd, uint creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemCode, uint devInst, uint flags);
}
