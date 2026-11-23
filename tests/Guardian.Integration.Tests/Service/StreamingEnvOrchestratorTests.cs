using System.Collections.Concurrent;
using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Service;
using Xunit;

namespace Guardian.Integration.Tests.Service;

/// <summary>
/// 服务端编排层测试：一键动作必须"立即返回 + 后台推进度"，且"夺回物理主显"只能凭客户端回执判定成功。
/// 端口用可控闸门替真机动作（装驱动/下载 33 MB 不该出现在单元测试里）。
/// </summary>
public sealed class StreamingEnvOrchestratorTests
{
    [Fact]
    public async Task Start_ReturnsBusyWithoutWaitingForTheSteps()
    {
        var port = new GatedPort();
        var orchestrator = NewOrchestrator(port);

        var started = orchestrator.Start(StreamingEnvOp.Enable, "test");
        Assert.Equal(StreamingEnvState.Busy, started.State);
        Assert.Equal(StreamingEnvOp.Enable, started.RunningOp);
        Assert.False(started.CanEnable);

        Assert.True(WaitUntil(() => port.Calls.Contains("config"), 3000), "后台任务没有开始推进");

        // 闸门还堵着：调用方却已经返回了，说明进度只能靠后续轮询拿。
        Assert.DoesNotContain("restart", port.Calls);
        Assert.Equal(StreamingEnvState.Busy, orchestrator.Refresh().State);

        port.Release();
        await WaitUntilDoneAsync(orchestrator);

        Assert.Contains("restart", port.Calls);
        Assert.NotEqual(StreamingEnvState.Busy, orchestrator.Refresh().State);
    }

    [Fact]
    public async Task RepeatedClicks_DoNotQueueASecondRun()
    {
        var port = new GatedPort();
        var orchestrator = NewOrchestrator(port);

        WaitUntil(() => orchestrator.Start(StreamingEnvOp.Enable, "click-1").State == StreamingEnvState.Busy, 3000);
        var second = orchestrator.Start(StreamingEnvOp.Enable, "click-2");
        var third = orchestrator.Start(StreamingEnvOp.Enable, "click-3");

        Assert.Equal(StreamingEnvState.Busy, second.State);
        Assert.Equal(StreamingEnvState.Busy, third.State);

        port.Release();
        await WaitUntilDoneAsync(orchestrator);

        // 三次点击只对应一次实际执行：整条启用序列只出现一遍（两阶段写 + 两次重启）。
        Assert.Equal(2, port.Calls.Count(call => call == "config"));
        Assert.Equal(2, port.Calls.Count(call => call == "restart"));
    }

    [Fact]
    public async Task RequestPhysicalRestore_SucceedsOnlyOnClientAcknowledgement()
    {
        var store = new StreamingTopologyStore();
        var orchestrator = NewOrchestrator(new GatedPort(), store, TimeSpan.FromMilliseconds(400));

        var unconfirmed = await orchestrator.RequestPhysicalRestoreAsync(CancellationToken.None);
        Assert.False(unconfirmed.Ok);
        Assert.Contains("没有收到回执", unconfirmed.Evidence);

        // 超时必须留着待办：客户端只是慢了一步，回来后仍该把物理屏夺回来。
        Assert.True(store.RestorePending);

        var pending = orchestrator.RequestPhysicalRestoreAsync(CancellationToken.None);
        await Task.Delay(60);
        store.Publish(new StreamingTopologyFact
        {
            PhysicalDisplayActive = true,
            ActiveMonitorCount = 2,
            ReportedAt = DateTimeOffset.Now,
        });

        var confirmed = await pending;
        Assert.True(confirmed.Ok);
        Assert.False(store.RestorePending);
    }

    [Fact]
    public void ReportTopology_CarriesThePendingFlagBackToTheClient()
    {
        var store = new StreamingTopologyStore();
        var orchestrator = NewOrchestrator(new GatedPort(), store);

        store.RequestPhysicalRestore();
        var profile = orchestrator.ReportTopology(new StreamingTopologyPayload { PhysicalDisplayActive = false, ActiveMonitorCount = 1 });

        Assert.True(profile.PhysicalRestorePending);
        Assert.False(store.Current()!.PhysicalDisplayActive);
        Assert.Equal(1, store.Current()!.ActiveMonitorCount);
    }

    [Fact]
    public void TopologyStore_IgnoreLateReportsFromThePast()
    {
        var store = new StreamingTopologyStore();
        var now = DateTimeOffset.Now;

        store.Publish(new StreamingTopologyFact { PhysicalDisplayActive = true, ReportedAt = now });
        store.Publish(new StreamingTopologyFact { PhysicalDisplayActive = false, ReportedAt = now - TimeSpan.FromSeconds(2) });

        Assert.True(store.Current()!.PhysicalDisplayActive);
    }

    [Fact]
    public void Refresh_DuringRun_ExposesFreshAttachPendingFlag()
    {
        var store = new StreamingTopologyStore();
        var orchestrator = NewOrchestrator(new GatedPort(), store);

        orchestrator.Start(StreamingEnvOp.Enable, "test");
        Assert.True(WaitUntil(() => orchestrator.Refresh().State == StreamingEnvState.Busy, 3000));

        // 接屏请求发生在进度发布之后（真实流程正是如此），客户端此刻轮询必须立刻看到待办，
        // 否则 60 秒等待窗口内客户端永远不知道要接屏（实测：两台机器都因此白等超时）。
        store.RequestVirtualAttach();
        var duringRun = orchestrator.Refresh();

        Assert.True(duringRun.AttachVirtualDisplayPending);
    }

    [Fact]
    public async Task RequestVirtualAttach_SucceedsOnlyOnClientAcknowledgement()
    {
        var store = new StreamingTopologyStore();
        var orchestrator = NewOrchestrator(new GatedPort(), store, attachWait: TimeSpan.FromMilliseconds(300));

        store.RequestVirtualAttach();
        var pending = orchestrator.RequestVirtualAttachAsync(CancellationToken.None);
        await Task.Delay(60);
        store.Publish(new StreamingTopologyFact
        {
            VirtualDisplayEnumerated = true,
            ActiveMonitorCount = 2,
            ReportedAt = DateTimeOffset.Now,
        });

        var confirmed = await pending;
        Assert.True(confirmed.Ok);
        Assert.False(store.VirtualAttachPending);
    }

    [Fact]
    public async Task RequestVirtualAttach_TimeoutClearsThePendingFlag()
    {
        var store = new StreamingTopologyStore();
        var orchestrator = NewOrchestrator(new GatedPort(), store, attachWait: TimeSpan.FromMilliseconds(200));

        var unconfirmed = await orchestrator.RequestVirtualAttachAsync(CancellationToken.None);

        Assert.False(unconfirmed.Ok);
        Assert.Contains("没有收到回执", unconfirmed.Evidence);
        // 超时必须清掉待办：留着它会让客户端在一键启用结束后无限重试接屏、
        // 每次都动一次显示拓扑（旧版实测就是这个循环刷屏）。
        Assert.False(store.VirtualAttachPending);
    }

    private static StreamingEnvOrchestrator NewOrchestrator(
        IStreamingEnvironmentPort port,
        StreamingTopologyStore? store = null,
        TimeSpan? restoreWait = null,
        TimeSpan? attachWait = null) =>
        new(store ?? new StreamingTopologyStore(), port, new SystemClock(), new NullLog(), restoreWait, attachWait);

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static Task WaitUntilDoneAsync(StreamingEnvOrchestrator orchestrator) =>
        Task.Run(() => Assert.True(WaitUntil(() => orchestrator.Refresh().State != StreamingEnvState.Busy, 5000), "一键动作没有在期限内结束"));

    private sealed class NullLog : IGuardianLog
    {
        public void Write(GuardianLogLevel level, string category, string message, string? detail = null)
        {
        }
    }

    /// <summary>把"写配置"这一步堵在闸门上，用来观察调用方是否被按住、以及重复点击是否重入。</summary>
    private sealed class GatedPort : IStreamingEnvironmentPort
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 后台线程写、测试线程轮询读：用并发队列，别用 List 边写边读。
        public ConcurrentQueue<string> Calls { get; } = new();

        public void Release() => _gate.TrySetResult(true);

        public StreamingEnvironmentProfile Probe() => new()
        {
            State = StreamingEnvState.Reverted,
            GeneratedAt = DateTimeOffset.Now,
            StampMs = Environment.TickCount64,
            Sunshine = new SunshineEnvironmentInfo { Installed = true, Version = "2026.914.233613" },
            VirtualDisplay = new VirtualDisplayEnvironmentInfo { DevicePresent = true, DeviceEnabled = true, Enumerated = true, PathActive = true },
        };

        public Task<StreamingEnvOutcome> EnsureSunshineInstalledAsync(CancellationToken token) => Step("sunshine");

        public Task<StreamingEnvOutcome> EnsureVirtualDisplayInstalledAsync(CancellationToken token) => Step("driver");

        public Task<StreamingEnvOutcome> EnsureDriverSettingsAsync(CancellationToken token) => Step("settings");

        public Task<StreamingEnvOutcome> EnsureVirtualDisplayInDesktopAsync(CancellationToken token) => Step("attach");

        public Task<StreamingEnvOutcome> SetVirtualDisplayEnabledAsync(bool enabled, CancellationToken token) => Step(enabled ? "enable" : "disable");

        public Task<StreamingEnvOutcome> ApplySunshineConfigurationAsync(SunshineConfigMode mode, CancellationToken token) =>
            Step(mode == SunshineConfigMode.Revert ? "revert" : "config");

        public Task<StreamingEnvOutcome> RestartSunshineAsync(CancellationToken token) => Step("restart");

        public Task<StreamingEnvOutcome> RestorePhysicalDisplayAsync(CancellationToken token) => Step("restore");

        private async Task<StreamingEnvOutcome> Step(string name)
        {
            Calls.Enqueue(name);
            if (name == "config")
            {
                await _gate.Task.ConfigureAwait(false);
            }

            return new StreamingEnvOutcome(true, $"{name} 完成");
        }
    }
}
