using Guardian.Windows.Streaming;
using Xunit;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// 只读冒烟：验证 SetupAPI/CfgMgr32 的 P/Invoke 组装与 Sunshine 路径定位在本机给出的实测结论。
/// 前提是本机已装 Sunshine 与 VDD（开发机事实），一旦结构体封送出错这些断言就会失败。
/// </summary>
public sealed class StreamingEnvironmentProbeTests
{
    [Fact]
    public void VirtualDisplayDevice_IsFoundByHardwareId()
    {
        var state = VirtualDisplayDevice.Probe();

        Assert.True(state.Present, state.Evidence);
        Assert.StartsWith(@"ROOT\DISPLAY", state.InstanceId);
        Assert.Contains("存在", state.Evidence);
        Assert.DoesNotContain("问题码 28", state.Evidence);
    }

    [Fact]
    public void VirtualDisplayDevice_UnknownHardwareId_ListsWhatWasSeen()
    {
        var state = VirtualDisplayDevice.Probe(@"Root\NoSuchDevice");

        Assert.False(state.Present);
        Assert.Contains("未找到", state.Evidence);
    }

    [Fact]
    public void EnableParamsStructure_IsAcceptedBySetupApi()
    {
        // 锁死 DIF 码与 SP_PROPCHANGE_PARAMS 布局：写错时 SetupDiSetClassInstallParams 回"参数错误"，
        // 实测后果是"一键恢复"里禁用虚拟设备那一步失败（DIF_PROPERTYCHANGE 必须是 0x12，不是凭记忆的 0x0B）。
        var evidence = VirtualDisplayDevice.TryValidateEnableParams();

        Assert.StartsWith("变更参数结构被接受", evidence);
    }

    [Fact]
    public void SunshineProbe_LocatesInstallAndConfig()
    {
        var snapshot = SunshineEnvironmentProbe.Read();

        Assert.True(snapshot.Found, snapshot.How);
        Assert.True(snapshot.ExecutableExists);
        Assert.True(snapshot.ConfigFileExists);
        Assert.NotEmpty(snapshot.Version);
        Assert.True(snapshot.ServiceExists);
        Assert.EndsWith("sunshine.conf", snapshot.ConfigFilePath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(snapshot.ConfigFilePath)!, "sunshine.log"), snapshot.LogFilePath);
    }

    [Fact]
    public void SunshineLog_IsParseableAndGuidsAreWellFormed()
    {
        var snapshot = SunshineEnvironmentProbe.Read();

        // 这里只锁"日志能被解析 + 解析结果形态合法"。此刻日志里有没有虚拟屏取决于机器处于哪种形态
        // （一键恢复后 Sunshine 重启，虚拟屏不在桌面布局里，清单就只剩物理屏），那不是被测逻辑；
        // 解析与判定规则本身由 SunshineDisplayDeviceLogTests 用合成日志锁死。
        Assert.True(SunshineDisplayDeviceLog.TryReadLatest(snapshot.LogFilePath, out var devices, out var readEvidence), readEvidence);
        Assert.NotEmpty(devices);
        Assert.All(devices, device => Assert.StartsWith("{", device.DeviceId));

        var virtualDevice = SunshineDisplayDeviceLog.FindVirtual(devices, out var findEvidence);
        if (virtualDevice is null)
        {
            Assert.Contains("清单", findEvidence);
        }
        else
        {
            Assert.True(SunshineConfigEditor.IsPlausibleOutputName(virtualDevice.DeviceId));
        }
    }
}
