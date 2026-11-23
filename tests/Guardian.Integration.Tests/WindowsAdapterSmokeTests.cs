using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Windows.Input;
using Guardian.Windows.Power;
using Guardian.Windows.SystemState;
using Xunit;

namespace Guardian.Integration.Tests;

/// <summary>真实 Windows 适配器冒烟测试（不涉及显示器关闭与钩子安装，安全）。</summary>
public class WindowsAdapterSmokeTests
{
    [Fact]
    public void SystemStateReader_ReadsPower()
    {
        var reader = new SystemStateReader();
        var power = reader.ReadPower();

        Assert.True(power.MonitorCount >= 0);
        // 不强制断言 AC/电池（依赖运行环境）。
    }

    [Fact]
    public void SystemStateReader_BootTime_IsReasonable()
    {
        var reader = new SystemStateReader();
        var boot = reader.GetSystemBootTime();

        Assert.True(boot <= DateTimeOffset.Now);
        Assert.True(DateTimeOffset.Now - boot < TimeSpan.FromDays(365));
    }

    [Fact]
    public void InputCapabilityDetector_EnumeratesWithoutThrowing()
    {
        var devices = InputCapabilityDetector.Detect();

        // 结果依赖机器；至少不抛异常，且计数非负。
        Assert.All(devices, d => Assert.True(d.Count >= 1));
    }

    [Fact]
    public void HotkeyMap_ResolvesDefaults()
    {
        Assert.Equal(0x7B, HotkeyMap.Resolve("F12"));
        Assert.Equal("F12", HotkeyMap.Normalize("unknown-key"));
        Assert.Contains("F12", HotkeyMap.SupportedKeys);
    }

    [Fact]
    public void PowerRequestController_ApplyThenRelease()
    {
        using var controller = new PowerRequestController();

        controller.Apply(keepDisplayOn: false);
        WaitUntil(() => controller.IsActive);
        Assert.True(controller.IsActive);

        controller.Release();
        WaitUntil(() => !controller.IsActive);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void Engine_WithRealPowerAdapter_AppliesAndReleases()
    {
        using var power = new PowerRequestController();
        var harness = new TestHarness(power);

        var result = harness.Engine.EnableProtection(ProtectionMode.Privacy, "test", displayPrivacyCheckAcknowledged: true);

        Assert.True(result.Success);
        WaitUntil(() => power.IsActive);

        harness.Engine.DisableProtection("test");
        WaitUntil(() => !power.IsActive);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(25);
        }
        Assert.Fail("等待条件超时");
    }

    /// <summary>真实电源适配器 + 假显示/输入（避免在测试中关闭真实显示器或安装钩子）。</summary>
    private sealed class TestHarness : IDisposable
    {
        public ProtectionEngine Engine { get; }

        public TestHarness(IPowerRequestController power)
        {
            Engine = new ProtectionEngine(
                new NullLog(),
                new SystemClock(),
                new FakeDisplayAdapter(),
                power,
                new FakeInputAdapter(),
                new SystemStateReader(),
                new NullSink(),
                new GuardianSettings());
            Engine.InitializeCapabilities(new CapabilityReport { GeneratedAt = DateTimeOffset.Now });
        }

        public void Dispose() => Engine.Dispose();

        private sealed class NullLog : IGuardianLog
        {
            public void Write(GuardianLogLevel level, string category, string message, string? detail = null) { }
        }

        private sealed class NullSink : IGuardianEventSink
        {
            public void OnLog(GuardianLogLevel level, string category, string message, string? detail = null) { }
            public void OnRecovery(RecoveryRecord record) { }
            public void OnSessionChanged(SessionInfo session) { }
            public void OnSettingsChanged(GuardianSettings settings) { }
            public void OnCapabilityChanged(CapabilityReport report) { }
        }

        private sealed class FakeDisplayAdapter : IDisplayPrivacyController
        {
            public bool IsActive { get; private set; }
            public void Enable() => IsActive = true;
            public void Disable() => IsActive = false;
        }

        private sealed class FakeInputAdapter : IInputIsolationController
        {
            public bool IsSupported => true;
            public bool IsActive { get; private set; }
            public void Enable(Action onEmergency, string emergencyHotkeyKey) => IsActive = true;
            public void Disable() => IsActive = false;
        }
    }
}
