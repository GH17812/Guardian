using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Windows.Display;
using Guardian.Windows.Streaming;
using Xunit;
using Xunit.Abstractions;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// 真实全链路回归：真实适配器 + 真实设备启停 + 真实 Sunshine 配置与重启，执行
/// "一键恢复 → 一键启用（含接屏进桌面新步骤）→ 一键恢复"完整往返。
/// 需要管理员权限（设备启停、pnputil、服务重启），且会把虚拟屏短暂接入/移出桌面，
/// 因此按环境变量 GUARDIAN_LIVE_ROUNDTRIP=1 显式启用；未启用时本测试不执行任何断言，
/// 这本身是诚实的 —— 它从来不属于普通测试套件。
/// </summary>
public sealed class StreamingEnvironmentRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public StreamingEnvironmentRoundTripTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Live_FullRoundTrip_RevertEnableRevert()
    {
        if (Environment.GetEnvironmentVariable("GUARDIAN_LIVE_ROUNDTRIP") != "1")
        {
            return;
        }

        var port = new StreamingEnvironmentService(() =>
        {
            var facts = SessionDisplayFactsReader.Read();
            return new StreamingTopologyFact
            {
                VirtualDisplayEnumerated = facts.VirtualDisplayEnumerated,
                VirtualDisplayPathActive = facts.VirtualDisplayPathActive,
                PhysicalDisplayActive = facts.PhysicalDisplayActive,
                ActiveMonitorCount = facts.ActiveMonitorCount,
                ReportedAt = DateTimeOffset.Now,
            };
        })
        {
            RestorePhysical = _ => Task.FromResult(
                DisplayReattacher.ReattachPhysicalDisplays(() => false)
                    ? new StreamingEnvOutcome(true, "物理屏重新挂载成功")
                    : new StreamingEnvOutcome(false, "物理屏重新挂载失败")),
            AttachVirtualDisplay = _ => Task.FromResult(
                DisplayReattacher.AttachVirtualDisplayToDesktop()
                    ? new StreamingEnvOutcome(true, "虚拟屏已接进桌面")
                    : new StreamingEnvOutcome(false, "虚拟屏接屏失败")),
            OfflineDirectoryProvider = () => null,
        };

        var log = new CollectingLog(_output);
        var coordinator = new StreamingEnvCoordinator(port, new SystemClock(), log);

        // 1) 先恢复：无论起点是什么形态，都回到"不接管 + 物理屏形态"的统一基线。
        var reverted = await coordinator.RevertAsync();
        Dump("恢复基线", reverted);
        Assert.Equal(StreamingEnvState.Reverted, reverted.State);

        // 2) 一键启用：真实设备/驱动/配置/接屏/两阶段接管。
        var enabled = await coordinator.EnableAsync();
        Dump("一键启用", enabled);
        Assert.True(enabled.State == StreamingEnvState.Ready,
            "启用未到 Ready。步骤取证：" + string.Join(" | ", enabled.Steps.Select(s => $"{s.Step}={s.Outcome}")));

        // 3) 再恢复：把机器交还给用户（虚拟屏禁用、配置还原、物理屏回来）。
        var back = await coordinator.RevertAsync();
        Dump("最终恢复", back);
        Assert.Equal(StreamingEnvState.Reverted, back.State);
    }

    private void Dump(string label, StreamingEnvironmentProfile profile)
    {
        _output.WriteLine($"=== {label} => {profile.State} ===");
        foreach (var step in profile.Steps)
        {
            _output.WriteLine($"  {step.Step,-26} {step.Outcome,-9} {step.Evidence}");
        }

        _output.WriteLine($"  BlockedReason: {profile.BlockedReason}");
        _output.WriteLine($"  Sunshine: installed={profile.Sunshine.Installed} configured={profile.Sunshine.Configured} effective={profile.Sunshine.EffectiveConfigured} activating={profile.Sunshine.Activating}");
        _output.WriteLine($"  Virtual:  present={profile.VirtualDisplay.DevicePresent} enabled={profile.VirtualDisplay.DeviceEnabled} path={profile.VirtualDisplay.PathActive} inDesktop={profile.VirtualDisplay.Enumerated} physical={profile.VirtualDisplay.PhysicalDisplayActive?.ToString() ?? "unknown"}");
    }

    private sealed class CollectingLog : IGuardianLog
    {
        private readonly ITestOutputHelper _output;

        public CollectingLog(ITestOutputHelper output) => _output = output;

        public void Write(GuardianLogLevel level, string category, string message, string? detail = null)
            => _output.WriteLine($"[{level}] {category}: {message}{(detail is null ? string.Empty : " | " + detail)}");
    }
}
