using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Ports;

namespace Guardian.Core.Tests.TestDoubles;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

public sealed class FakeLog : IGuardianLog
{
    public List<(GuardianLogLevel Level, string Category, string Message, string? Detail)> Entries { get; } = new();

    public void Write(GuardianLogLevel level, string category, string message, string? detail = null)
        => Entries.Add((level, category, message, detail));
}

public sealed class FakeDisplay : IDisplayPrivacyController
{
    public bool IsActive { get; private set; }
    public int EnableCalls { get; private set; }
    public int DisableCalls { get; private set; }
    public bool ThrowOnEnable { get; set; }
    public bool ThrowOnDisable { get; set; }

    public void Enable()
    {
        EnableCalls++;
        if (ThrowOnEnable) throw new InvalidOperationException("display enable failed");
        IsActive = true;
    }

    public void Disable()
    {
        DisableCalls++;
        if (ThrowOnDisable) throw new InvalidOperationException("display disable failed");
        IsActive = false;
    }
}

public sealed class FakePower : IPowerRequestController
{
    public bool IsActive { get; private set; }
    public bool? LastKeepDisplayOn { get; private set; }
    public int ApplyCalls { get; private set; }
    public int ReleaseCalls { get; private set; }
    public bool AwayMode { get; private set; }

    public void Apply(bool keepDisplayOn)
    {
        ApplyCalls++;
        LastKeepDisplayOn = keepDisplayOn;
        IsActive = true;
    }

    public void Release()
    {
        ReleaseCalls++;
        IsActive = false;
    }

    public void SetAwayMode(bool enabled) => AwayMode = enabled;
}

public sealed class FakeInput : IInputIsolationController
{
    public bool IsSupported { get; set; } = true;
    public bool IsActive { get; private set; }
    public Action? EmergencyCallback { get; private set; }
    public string? LastHotkeyKey { get; private set; }
    public int EnableCalls { get; private set; }
    public int DisableCalls { get; private set; }
    public bool ThrowOnEnable { get; set; }

    public void Enable(Action onEmergency, string emergencyHotkeyKey)
    {
        EnableCalls++;
        if (ThrowOnEnable) throw new InvalidOperationException("input enable failed");
        EmergencyCallback = onEmergency;
        LastHotkeyKey = emergencyHotkeyKey;
        IsActive = true;
    }

    public void Disable()
    {
        DisableCalls++;
        IsActive = false;
    }
}

public sealed class FakeSystem : ISystemStateReader
{
    public SystemPowerReading Power { get; set; } = new(true, null, 2);
    public bool RemoteSession { get; set; }
    public DateTimeOffset BootTime { get; set; } = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public SystemPowerReading ReadPower() => Power;

    public bool IsRemoteSession() => RemoteSession;

    public DateTimeOffset GetSystemBootTime() => BootTime;
}

public sealed class FakeSink : IGuardianEventSink
{
    public List<RecoveryRecord> Recoveries { get; } = new();
    public List<SessionInfo> SessionChanges { get; } = new();
    public List<GuardianSettings> SettingsChanges { get; } = new();
    public List<CapabilityReport> CapabilityChanges { get; } = new();

    public void OnLog(GuardianLogLevel level, string category, string message, string? detail = null) { }

    public void OnRecovery(RecoveryRecord record) => Recoveries.Add(record);

    public void OnSessionChanged(SessionInfo session) => SessionChanges.Add(session);

    public void OnSettingsChanged(GuardianSettings settings) => SettingsChanges.Add(settings);

    public void OnCapabilityChanged(CapabilityReport report) => CapabilityChanges.Add(report);
}

/// <summary>
/// 可脚本化的串流环境端口：动作会真实改动"被探测到的事实"，
/// 因此测试断言的是状态迁移与调用顺序，而不是单纯的调用次数。
/// </summary>
public sealed class FakeStreamingEnvironment : IStreamingEnvironmentPort
{
    public const string InstallSunshine = "install-sunshine";
    public const string InstallDevice = "install-device";
    public const string EnableDevice = "enable-device";
    public const string DisableDevice = "disable-device";
    public const string ConfigOn = "config-takeover";
    public const string ConfigActivate = "config-activate";
    public const string ConfigOff = "config-revert";
    public const string Restart = "restart";
    public const string RestorePhysical = "restore-physical";
    public const string SeedSettings = "seed-settings";
    public const string AttachVirtual = "attach-virtual";

    /// <summary>建屏设置是否已放置（断言"设备已存在时也要落位"这条新规则）。</summary>
    public bool SettingsSeeded { get; set; }

    public bool SunshineInstalled { get; set; } = true;
    public bool DevicePresent { get; set; } = true;
    public bool DeviceEnabled { get; set; }
    public bool Configured { get; set; }
    public bool EffectiveConfigured { get; set; }
    public bool Activating { get; set; }
    public bool EffectiveActivating { get; set; }
    public bool PhysicalActive { get; set; } = true;

    /// <summary>虚拟适配器有可用显示路径（对应 EnumDisplayDevices 的 ACTIVE|ATTACHED）。</summary>
    public bool PathActive { get; set; } = true;

    /// <summary>虚拟屏已在桌面布局里（对应 EnumDisplayMonitors 能看到），也就是真能当屏幕用。</summary>
    public bool InDesktop { get; set; } = true;

    /// <summary>ensure_primary 生效后 Sunshine 是否真把该路径拉进桌面（关掉即可模拟"激活失败"的场景）。</summary>
    public bool ActivateBringsIntoDesktop { get; set; } = true;

    /// <summary>客户端接屏是否成功（关掉即可模拟"路径有但进不了桌面"的场景）。</summary>
    public bool AttachBringsIntoDesktop { get; set; } = true;

    /// <summary>重启设备是否会让驱动造出显示路径（关掉即可模拟"驱动零监视器"的新机场景）。</summary>
    public bool DeviceRestartCreatesPath { get; set; } = true;

    public List<string> Calls { get; } = new();
    public HashSet<string> Failures { get; } = new();
    public HashSet<string> Exceptions { get; } = new();
    public Dictionary<string, TaskCompletionSource<bool>> Gates { get; } = new();

    public StreamingEnvironmentProfile Probe() => new()
    {
        Sunshine = new SunshineEnvironmentInfo
        {
            Installed = SunshineInstalled,
            Version = SunshineInstalled ? "2026.914.233613" : string.Empty,
            ServiceRunning = SunshineInstalled,
            Configured = Configured,
            EffectiveConfigured = EffectiveConfigured,
            Activating = EffectiveActivating,
        },
        VirtualDisplay = new VirtualDisplayEnvironmentInfo
        {
            DevicePresent = DevicePresent,
            DeviceEnabled = DeviceEnabled,
            Enumerated = InDesktop,
            PathActive = PathActive,
            InstanceId = @"ROOT\DISPLAY\0000",
            FriendlyName = "Virtual Display Driver",
            OutputGuid = "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}",
            PhysicalDisplayActive = PhysicalActive,
        },
    };

    public Task<StreamingEnvOutcome> EnsureSunshineInstalledAsync(CancellationToken token) =>
        Act(InstallSunshine, () => SunshineInstalled = true, "msiexec 退出码 0");

    public Task<StreamingEnvOutcome> EnsureVirtualDisplayInstalledAsync(CancellationToken token) =>
        Act(InstallDevice, () => DevicePresent = true, @"已创建根设备 ROOT\DISPLAY\0000");

    public Task<StreamingEnvOutcome> EnsureVirtualDisplayInDesktopAsync(CancellationToken token)
    {
        // 接屏失败的场景要真的返回失败，而不是"成功但什么都没发生"。
        if (!AttachBringsIntoDesktop)
        {
            return Task.FromResult(new StreamingEnvOutcome(false, "客户端接屏未成功（模拟）"));
        }

        return Act(AttachVirtual, () => InDesktop = true, "已把虚拟屏接进桌面");
    }

    public Task<StreamingEnvOutcome> EnsureDriverSettingsAsync(CancellationToken token) =>
        Act(SeedSettings, () => SettingsSeeded = true, "已放置 vdd_settings.xml");

    public Task<StreamingEnvOutcome> SetVirtualDisplayEnabledAsync(bool enabled, CancellationToken token) =>
        Act(enabled ? EnableDevice : DisableDevice, () =>
        {
            DeviceEnabled = enabled;

            // 驱动只在设备启动时按自己的设置创建监视器对象：启用后路径才可能出现（新机实测的期望行为）。
            if (enabled && DeviceRestartCreatesPath)
            {
                PathActive = true;
            }
        }, enabled ? "设备已启用" : "设备已禁用");

    public Task<StreamingEnvOutcome> ApplySunshineConfigurationAsync(SunshineConfigMode mode, CancellationToken token) => mode switch
    {
        SunshineConfigMode.Activate => Act(ConfigActivate, () =>
        {
            Configured = true;
            Activating = true;
        }, "写入 ensure_primary"),
        SunshineConfigMode.Takeover => Act(ConfigOn, () =>
        {
            Configured = true;
            Activating = false;
        }, "写入 ensure_only_display"),
        _ => Act(ConfigOff, () =>
        {
            Configured = false;
            Activating = false;
        }, "已还原备份"),
    };

    // 服务重启才把 dd_config 落到拓扑上：ensure_primary 会把可用路径拉进桌面（前提是有路径），
    // ensure_only_display 还会停掉物理屏。模拟这条因果，测试断言的才是状态迁移而不是调用次数。
    public Task<StreamingEnvOutcome> RestartSunshineAsync(CancellationToken token) =>
        Act(Restart, () =>
        {
            EffectiveConfigured = Configured;
            EffectiveActivating = Activating;
            if (EffectiveConfigured && EffectiveActivating && PathActive && ActivateBringsIntoDesktop)
            {
                InDesktop = true;
            }
        }, "服务已进入 Running");

    public Task<StreamingEnvOutcome> RestorePhysicalDisplayAsync(CancellationToken token) =>
        Act(RestorePhysical, () => PhysicalActive = true, "物理面板为活动且主显");

    private async Task<StreamingEnvOutcome> Act(string name, Action apply, string evidence)
    {
        Calls.Add(name);
        if (Gates.TryGetValue(name, out var gate))
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (Exceptions.Contains(name))
        {
            throw new InvalidOperationException($"{name} 抛出异常");
        }

        if (Failures.Contains(name))
        {
            return new StreamingEnvOutcome(false, $"{name} 模拟失败");
        }

        apply();
        return new StreamingEnvOutcome(true, evidence);
    }
}

/// <summary>组装被测引擎的测试夹具。</summary>
public sealed class EngineHarness : IDisposable
{
    public FakeClock Clock { get; } = new();
    public FakeLog Log { get; } = new();
    public FakeDisplay Display { get; } = new();
    public FakePower Power { get; } = new();
    public FakeInput Input { get; } = new();
    public FakeSystem System { get; } = new();
    public FakeSink Sink { get; } = new();
    public ProtectionEngine Engine { get; }

    public EngineHarness(GuardianSettings? settings = null)
    {
        Engine = new ProtectionEngine(Log, Clock, Display, Power, Input, System, Sink, settings ?? new GuardianSettings());
        var report = new CapabilityReport
        {
            GeneratedAt = Clock.UtcNow.ToLocalTime(),
            RemoteInputDetection = RemoteInputDetectionCapability.Supported,
        };
        Engine.InitializeCapabilities(report);
    }

    public void SetDisplayPrivacyValidated(bool ok)
        => Engine.ConfirmDisplayPrivacyCheck(new ConfirmDisplayPrivacyCheckPayload { RemoteCaptureOk = ok });

    public void Dispose() => Engine.Dispose();
}
