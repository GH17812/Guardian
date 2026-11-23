using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Tests.TestDoubles;
using Xunit;

namespace Guardian.Core.Tests.Engine;

/// <summary>
/// 串流环境状态机测试。重点是三条承诺：互锁绝不写出"没有可用输出"的配置；
/// 中间态不得冒充完成态；一键卸载不得把驱动删掉。
/// </summary>
public class StreamingEnvCoordinatorTests
{
    private static (StreamingEnvCoordinator Coordinator, FakeStreamingEnvironment Port, FakeLog Log) Create(
        Action<FakeStreamingEnvironment>? configure = null,
        int desktopWaitAttempts = 10)
    {
        var port = new FakeStreamingEnvironment();
        configure?.Invoke(port);
        var log = new FakeLog();

        // 等待窗口在生产里是 10×600ms；测试用 10ms 步进，语义不变但不拖慢。
        return (new StreamingEnvCoordinator(port, new FakeClock(), log, desktopWaitAttempts, TimeSpan.FromMilliseconds(10)), port, log);
    }

    [Fact]
    public async Task Enable_AllFactsPresent_ReachesReady_InRequiredOrder()
    {
        var (coordinator, port, _) = Create();

        var result = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvState.Ready, result.State);
        Assert.True(result.Sunshine.Configured);
        Assert.True(result.VirtualDisplay.DeviceEnabled);
        Assert.True(result.CanRevert);
        Assert.False(result.CanEnable);

        // 顺序本身就是安全条件：启用设备 → 只激活（不停物理屏）→ 重启 → 确认进桌面 → 才允许接管 → 再重启。
        Assert.Equal(new[]
        {
            FakeStreamingEnvironment.EnableDevice,
            FakeStreamingEnvironment.SeedSettings,   // 建屏设置必须在"等设备路径"之前落位
            FakeStreamingEnvironment.ConfigActivate,
            FakeStreamingEnvironment.Restart,
            FakeStreamingEnvironment.ConfigOn,
            FakeStreamingEnvironment.Restart,
        }, port.Calls);
    }

    [Fact]
    public async Task Enable_NoDisplayPath_InterlocksBeforeAnyConfigWrite()
    {
        // 最强的一条：连显示路径都没有时，两个阶段的配置都不许写。
        var (coordinator, port, _) = Create(p =>
        {
            p.PathActive = false;
            p.InDesktop = false;
            p.DeviceRestartCreatesPath = false;
        });

        var result = await coordinator.EnableAsync();

        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigActivate, port.Calls);
        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigOn, port.Calls);
        Assert.Equal(StreamingEnvState.Reverted, result.State);
        Assert.StartsWith(StreamingEnvCoordinator.InterlockNoDisplayPath, result.BlockedReason);

        // 拦停理由必须交代"等了多久"：设备启用是异步的，一眼就要能看出不是没等就否死。
        Assert.Contains("累计约", result.BlockedReason);
        Assert.Contains("厂商工具", result.BlockedReason);
        Assert.Equal(StreamingEnvStepOutcome.Blocked,
            result.Steps.Single(s => s.Step == StreamingEnvStep.VirtualDisplayEnumerated).Outcome);
        Assert.Equal(StreamingEnvStepOutcome.Blocked,
            result.Steps.Single(s => s.Step == StreamingEnvStep.SunshineActivating).Outcome);
    }

    [Fact]
    public async Task Enable_PathNotInDesktop_ClientAttachBringsItIn_CompletesTakeover()
    {
        // 新机实测场景：路径存在、桌面里没有它；客户端自己把屏接进桌面（ChangeDisplaySettingsEx），
        // 服务端收到回执后继续两阶段接管。这是"客户端连不上 Moonlight"时的唯一出路。
        var (coordinator, port, _) = Create(p =>
        {
            p.InDesktop = false;
            p.AttachBringsIntoDesktop = true;
        }, desktopWaitAttempts: 3);

        var result = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvState.Ready, result.State);
        Assert.Contains(FakeStreamingEnvironment.AttachVirtual, port.Calls);
        Assert.Contains(FakeStreamingEnvironment.ConfigActivate, port.Calls);
        Assert.Contains(FakeStreamingEnvironment.ConfigOn, port.Calls);
    }

    [Fact]
    public async Task Enable_ClientAttachFails_StopsBeforeAnyConfigWrite()
    {
        // 接屏失败 = 虚拟屏进不了桌面，此时绝不能写任何接管配置（哪怕 ensure_primary）。
        var (coordinator, port, _) = Create(p =>
        {
            p.InDesktop = false;
            p.AttachBringsIntoDesktop = false;
            p.ActivateBringsIntoDesktop = false;
        }, desktopWaitAttempts: 3);

        var result = await coordinator.EnableAsync();

        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigActivate, port.Calls);
        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigOn, port.Calls);
        Assert.Equal(StreamingEnvStepOutcome.Failed,
            result.Steps.Single(s => s.Step == StreamingEnvStep.VirtualDisplayAttached).Outcome);
        Assert.Contains("没能进入桌面布局", result.BlockedReason);

        // 停在这里时用户必须还能走回来。
        Assert.True(result.CanRevert);
    }

[Fact]
    public async Task Enable_NoPathAtFirst_DeviceRestartUnblocksIt()
    {
        // 新机实测的场景：根设备刚建出来时零监视器，互锁一先判死。
        // 现在必须先重启一次虚拟设备（只碰虚拟适配器、不停物理屏）再判死，否则会死锁在
        // "没路径→取不到 output_name→走不到自举"这个环里。
        var (coordinator, port, _) = Create(p =>
        {
            p.PathActive = false;
            p.InDesktop = false;
            p.DeviceRestartCreatesPath = true;
        });

        var result = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvState.Ready, result.State);
        Assert.Contains(FakeStreamingEnvironment.ConfigActivate, port.Calls);
        Assert.Contains(FakeStreamingEnvironment.ConfigOn, port.Calls);
    }

    [Fact]
    public async Task Enable_DeviceAlreadyPresent_StillSeedsDriverSettings()
    {
        // 新机 15:03 的教训：设备已存在时"安装驱动"整步跳过，而放置 vdd_settings.xml 依附在那一步里，
        // 于是驱动永远不建监视器、互锁永远拦停。建屏设置现在是独立一步，必须无条件落位。
        var (coordinator, port, _) = Create(p =>
        {
            p.DevicePresent = true;
            p.DeviceEnabled = true;
        });

        await coordinator.EnableAsync();

        Assert.True(port.SettingsSeeded);
        Assert.Contains(FakeStreamingEnvironment.SeedSettings, port.Calls);
    }

    [Fact]
    public async Task Enable_SunshineMissingAndInstallFails_StopsBeforeTouchingDisplay()
    {
        var (coordinator, port, _) = Create(p =>
        {
            p.SunshineInstalled = false;
            p.Failures.Add(FakeStreamingEnvironment.InstallSunshine);
        });

        var result = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvState.SunshineMissing, result.State);
        Assert.Equal(new[] { FakeStreamingEnvironment.InstallSunshine }, port.Calls);
        Assert.Contains("Sunshine", result.BlockedReason);
    }

    [Fact]
    public async Task Enable_ConfigWrittenButRestartFails_ReportsPartialFailure()
    {
        var (coordinator, port, _) = Create(p => p.Failures.Add(FakeStreamingEnvironment.Restart));

        var result = await coordinator.EnableAsync();

        // 第一阶段文件已写、进程还是旧形态：既不是"已就绪"也不是"未启用"，界面必须停在中间态。
        Assert.Equal(StreamingEnvState.PartialFailure, result.State);
        Assert.True(result.CanRevert);
        Assert.Contains(FakeStreamingEnvironment.ConfigActivate, port.Calls);

        // 激活阶段没能落地，就绝不能继续去做"停用物理屏"那一步。
        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigOn, port.Calls);
    }

    [Fact]
    public async Task Revert_CleanRun_EndsRevertedAndKeepsDriverInstalled()
    {
        var (coordinator, port, _) = Create(p =>
        {
            p.Configured = true;
            p.EffectiveConfigured = true;
            p.DeviceEnabled = true;
        });

        var result = await coordinator.RevertAsync();

        Assert.Equal(StreamingEnvState.Reverted, result.State);
        Assert.False(result.VirtualDisplay.DeviceEnabled);

        // 一键卸载的定义就是"不删驱动"：设备禁用但根设备仍在，下次一键启用无需再装一次。
        Assert.True(port.DevicePresent);
        Assert.True(result.VirtualDisplay.DevicePresent);
    }

    [Fact]
    public async Task Revert_RestoresConfigBeforeDisablingDevice()
    {
        var (coordinator, port, _) = Create(p =>
        {
            p.Configured = true;
            p.EffectiveConfigured = true;
            p.DeviceEnabled = true;
        });

        await coordinator.RevertAsync();

        Assert.True(port.Calls.IndexOf(FakeStreamingEnvironment.ConfigOff)
                    < port.Calls.IndexOf(FakeStreamingEnvironment.DisableDevice));
        Assert.True(port.Calls.IndexOf(FakeStreamingEnvironment.Restart)
                    < port.Calls.IndexOf(FakeStreamingEnvironment.DisableDevice));
    }

    [Fact]
    public async Task Revert_WhenRestartFails_StillRestoresPhysicalAndReportsPartialFailure()
    {
        var (coordinator, port, _) = Create(p =>
        {
            p.Configured = true;
            p.EffectiveConfigured = true;
            p.DeviceEnabled = true;
            p.Failures.Add(FakeStreamingEnvironment.Restart);
        });

        var result = await coordinator.RevertAsync();

        // 配置文件还原了但进程仍会接管拓扑 —— 判 PartialFailure 才算如实呈现。
        Assert.Equal(StreamingEnvState.PartialFailure, result.State);
        Assert.Contains(FakeStreamingEnvironment.RestorePhysical, port.Calls);
        Assert.Contains(FakeStreamingEnvironment.DisableDevice, port.Calls);
    }

    [Fact]
    public async Task Revert_SunshineMissing_PerformsNoActions()
    {
        var (coordinator, port, _) = Create(p => p.SunshineInstalled = false);

        var result = await coordinator.RevertAsync();

        Assert.Empty(port.Calls);
        Assert.Equal(StreamingEnvState.SunshineMissing, result.State);
    }

    [Fact]
    public async Task Enable_PortThrows_RecordsFailureWithoutCrashingCoordinator()
    {
        // 端口抛异常（不是返回失败）也必须被吃掉并记录，绝不能把协调器炸掉。
        var (coordinator, port, log) = Create(p => p.Exceptions.Add(FakeStreamingEnvironment.ConfigActivate));

        var result = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvStepOutcome.Failed,
            result.Steps.Single(s => s.Step == StreamingEnvStep.SunshineActivating).Outcome);

        // 写入本身抛异常 => 磁盘什么都没改，状态如实是未接管，而不是"部分成功"。
        Assert.Equal(StreamingEnvState.Reverted, result.State);
        Assert.DoesNotContain(FakeStreamingEnvironment.ConfigOn, port.Calls);
        Assert.Contains(log.Entries, e => e.Category == "streaming-env" && e.Level == Guardian.Contracts.GuardianLogLevel.Warning);
    }

    [Fact]
    public async Task Enable_SecondConcurrentCall_DoesNotExecuteTwice()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (coordinator, port, _) = Create(p => p.Gates[FakeStreamingEnvironment.ConfigOn] = gate);

        var first = coordinator.EnableAsync();
        while (!port.Calls.Contains(FakeStreamingEnvironment.ConfigOn))
        {
            await Task.Delay(10);
        }

        var second = await coordinator.EnableAsync();

        Assert.Equal(StreamingEnvState.Busy, second.State);
        Assert.Equal(1, port.Calls.Count(c => c == FakeStreamingEnvironment.ConfigOn));

        gate.SetResult(true);
        await first;
    }

    [Fact]
    public async Task Progress_EmitsNonDecreasingStampsAndEndsNotBusy()
    {
        var (coordinator, _, _) = Create();
        var emitted = new List<StreamingEnvironmentProfile>();
        coordinator.Progress += emitted.Add;

        var final = await coordinator.EnableAsync();

        Assert.True(emitted.Count >= 2);
        Assert.Equal(StreamingEnvState.Busy, emitted[0].State);
        for (var i = 1; i < emitted.Count; i++)
        {
            Assert.True(emitted[i].StampMs >= emitted[i - 1].StampMs, "进度戳必须单调，否则界面会被迟到的旧档案倒回去");
        }

        Assert.Null(final.RunningOp);
        Assert.Equal(StreamingEnvState.Ready, final.State);
    }

    [Fact]
    public void Refresh_IsReadOnly()
    {
        var (coordinator, port, _) = Create();

        var result = coordinator.Refresh();

        Assert.Empty(port.Calls);
        Assert.Equal(StreamingEnvState.Reverted, result.State);
    }
}
