using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Core.Tests.TestDoubles;
using Xunit;

namespace Guardian.Core.Tests.Engine;

public class ProtectionEngineTests
{
    private static EngineHarness CreateHarness(GuardianSettings? settings = null) => new(settings);

    // ---------------- 开启 / 解除 ----------------

    [Fact]
    public void Enable_Privacy_AppliesDisplayAndPower()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);

        var result = h.Engine.EnableProtection(ProtectionMode.Privacy, "test", displayPrivacyCheckAcknowledged: true);

        Assert.True(result.Success);
        Assert.True(h.Display.IsActive);
        Assert.True(h.Power.IsActive);
        Assert.False(h.Power.LastKeepDisplayOn); // 隐私模式：显示器可关闭
        Assert.False(h.Input.IsActive);
        Assert.Equal(ProtectionState.Active, h.Engine.GetSnapshot().State);
        Assert.Equal(ProtectionMode.Privacy, h.Engine.GetSnapshot().ActiveMode);
    }

    [Fact]
    public void Enable_WithoutDisplayValidation_RequiresConfirmation()
    {
        using var h = CreateHarness();

        var result = h.Engine.EnableProtection(ProtectionMode.Privacy, "test", displayPrivacyCheckAcknowledged: false);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConfirmationRequired, result.ErrorCode);
        Assert.False(h.Display.IsActive);
        Assert.False(h.Power.IsActive);
    }

    [Fact]
    public void Enable_WithAckButUnvalidated_Succeeds()
    {
        using var h = CreateHarness();

        var result = h.Engine.EnableProtection(ProtectionMode.Privacy, "test", displayPrivacyCheckAcknowledged: true);

        Assert.True(result.Success);
        Assert.True(h.Display.IsActive);
    }

    [Fact]
    public void Disable_ReleasesAll()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);

        var result = h.Engine.DisableProtection("test");

        Assert.True(result.Success);
        Assert.False(h.Display.IsActive);
        Assert.False(h.Power.IsActive);
        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.True(h.Power.ReleaseCalls >= 1);
    }

    // ---------------- 输入隔离 ----------------

    [Fact]
    public void Enable_Isolation_AppliesInputHooks()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);

        var result = h.Engine.EnableProtection(ProtectionMode.InputIsolation, "test", true);

        Assert.True(result.Success);
        Assert.True(h.Input.IsActive);
        Assert.Equal("F12", h.Input.LastHotkeyKey);
    }

    [Fact]
    public void Enable_Isolation_DuringRemoteSession_RejectedByDefault()
    {
        using var h = CreateHarness();
        h.System.RemoteSession = true;
        h.SetDisplayPrivacyValidated(true);

        var result = h.Engine.EnableProtection(ProtectionMode.InputIsolation, "test", true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.False(h.Input.IsActive);
    }

    [Fact]
    public void Enable_Isolation_DuringRemoteSession_AllowedWhenConfigured()
    {
        using var h = CreateHarness(new GuardianSettings { AllowIsolationDuringRemoteSession = true });
        h.System.RemoteSession = true;
        h.SetDisplayPrivacyValidated(true);

        var result = h.Engine.EnableProtection(ProtectionMode.InputIsolation, "test", true);

        Assert.True(result.Success);
        Assert.True(h.Input.IsActive);
    }

    [Fact]
    public void Enable_EnhancedIsolation_Unsupported()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);

        var result = h.Engine.EnableProtection(ProtectionMode.EnhancedIsolation, "test", true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.Unsupported, result.ErrorCode);
    }

    // ---------------- 紧急恢复 ----------------

    [Fact]
    public void Recover_ReleasesEverything_AndEndsSessions()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.InputIsolation, "test", true);
        h.Engine.CreateSession(new CreateSessionPayload { Name = "任务A", RequiredMode = ProtectionMode.Privacy });

        var result = h.Engine.Recover(RecoveryReason.EmergencyHotkey, RecoverySource.Local);

        Assert.True(result.Success);
        Assert.False(h.Input.IsActive);
        Assert.False(h.Display.IsActive);
        Assert.False(h.Power.IsActive);
        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.Equal(0, h.Engine.GetSnapshot().ActiveSessionCount);
        Assert.All(h.Engine.ListSessions(), s => Assert.Equal(SessionStatus.Ended, s.Status));
        Assert.Single(h.Sink.Recoveries);
        Assert.True(h.Sink.Recoveries[0].Success);
    }

    [Fact]
    public void Recover_WithFailingAdapter_MarksFailed_AndNeverThrows()
    {
        using var h = CreateHarness();
        h.Display.ThrowOnDisable = true; // 恢复步骤失败

        var result = h.Engine.Recover(RecoveryReason.UserRequest, RecoverySource.Local);

        Assert.True(result.Success); // 命令本身不失败
        Assert.Equal(ProtectionState.Failed, h.Engine.GetSnapshot().State); // 如实呈现
        Assert.False(h.Sink.Recoveries[0].Success);
    }

    [Fact]
    public void EmergencyHotkeyCallback_TriggersRecovery()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.InputIsolation, "test", true);

        h.Input.EmergencyCallback!.Invoke();

        // 回调经 Task.Run 异步触发恢复，轮询等待。
        WaitUntil(() => h.Engine.GetSnapshot().State == ProtectionState.Inactive);
        Assert.False(h.Input.IsActive);
    }

    // ---------------- 适配器失败 fail-open ----------------

    [Fact]
    public void AdapterFailure_OnApply_RecoversAutomatically()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Display.ThrowOnEnable = true;

        var result = h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InternalError, result.ErrorCode);
        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State); // 恢复成功 → Inactive
        Assert.False(h.Power.IsActive);
        Assert.Single(h.Sink.Recoveries);
    }

    // ---------------- 会话 ----------------

    [Fact]
    public void Sessions_AggregateMode_MaxWins()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);

        h.Engine.CreateSession(new CreateSessionPayload { Name = "远程", RequiredMode = ProtectionMode.Privacy });
        h.Engine.CreateSession(new CreateSessionPayload { Name = "Agent", RequiredMode = ProtectionMode.InputIsolation });

        var snapshot = h.Engine.GetSnapshot();
        Assert.Equal(ProtectionState.Active, snapshot.State);
        Assert.Equal(ProtectionMode.InputIsolation, snapshot.ActiveMode);
        Assert.Equal(2, snapshot.ActiveSessionCount);
        Assert.True(h.Input.IsActive);

        // 结束高等级会话后回落到 Privacy。
        var isolationSession = h.Engine.ListSessions().Single(s => s.Name == "Agent");
        h.Engine.EndSession(isolationSession.Id, SessionEndReason.UserRequest);

        var after = h.Engine.GetSnapshot();
        Assert.Equal(ProtectionMode.Privacy, after.ActiveMode);
        Assert.False(h.Input.IsActive);
        Assert.True(h.Display.IsActive);
    }

    [Fact]
    public void DisableManual_KeepsProtection_WhileSessionsActive()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.CreateSession(new CreateSessionPayload { Name = "编译", RequiredMode = ProtectionMode.Privacy });

        h.Engine.DisableProtection("test");

        var snapshot = h.Engine.GetSnapshot();
        Assert.False(snapshot.ManualProtectionEnabled);
        Assert.Equal(ProtectionState.Active, snapshot.State); // 会话仍维持保护
        Assert.True(h.Power.IsActive);
    }

    [Fact]
    public void SessionTtl_ExpiresAndReleasesProtection()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.CreateSession(new CreateSessionPayload
        {
            Name = "定时任务",
            RequiredMode = ProtectionMode.Privacy,
            MaxDurationMinutes = 10,
        });
        Assert.True(h.Power.IsActive);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.Engine.Tick();

        var snapshot = h.Engine.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveSessionCount);
        Assert.False(h.Power.IsActive);
        Assert.Equal(ProtectionState.Inactive, snapshot.State);
        Assert.All(h.Engine.ListSessions(), s => Assert.Equal(SessionEndReason.Timeout, s.EndReason));
    }

    // ---------------- 客户端心跳 ----------------

    [Fact]
    public void ClientLost_RecoversByDefault()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.OnClientHeartbeat();

        h.Clock.Advance(TimeSpan.FromSeconds(20)); // 默认阈值 15s
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.False(h.Display.IsActive);
        Assert.Equal(RecoveryReason.ClientLost, h.Engine.GetSnapshot().LastRecoveryReason);
    }

    [Fact]
    public void ClientLost_FiresRecoveryOnlyOncePerLoss()
    {
        // 客户端退出后，同一丢失事件只恢复一次，不随 tick 重复执行（避免日志刷屏与重复恢复）。
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.OnClientHeartbeat();

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Engine.Tick();
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        h.Engine.Tick();
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.Single(h.Sink.Recoveries, r => r.Reason == RecoveryReason.ClientLost);
    }

    [Fact]
    public void ClientLost_RecoveryRearms_AfterHeartbeatResumes()
    {
        // 心跳恢复后重新武装：下一次丢失仍能触发恢复。
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);

        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.OnClientHeartbeat();
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Engine.Tick();
        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);

        h.Engine.OnClientHeartbeat();
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.OnClientHeartbeat();
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.Equal(2, h.Sink.Recoveries.Count(r => r.Reason == RecoveryReason.ClientLost));
    }

    [Fact]
    public void ClientLost_WithNothingActive_DoesNotRecover()
    {
        // 无保护、无会话时客户端退出：不触发恢复，也不污染“最近操作”。
        using var h = CreateHarness();
        h.Engine.OnClientHeartbeat();

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Engine.Tick();

        Assert.Empty(h.Sink.Recoveries);
        Assert.NotEqual("recover", h.Engine.GetSnapshot().LastOperation);
    }

    [Fact]
    public void ClientLost_KeepsProtection_WhenConfigured()
    {
        using var h = CreateHarness(new GuardianSettings { UiLossAction = UiLossAction.KeepProtection });
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.Engine.OnClientHeartbeat();

        h.Clock.Advance(TimeSpan.FromSeconds(120));
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Active, h.Engine.GetSnapshot().State);
        Assert.True(h.Display.IsActive);
    }

    [Fact]
    public void NoHeartbeatEver_DoesNotTriggerRecovery()
    {
        // 无人值守场景：客户端从未连接过，不判定丢失。
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.CreateSession(new CreateSessionPayload { Name = "无人值守", RequiredMode = ProtectionMode.Privacy });

        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Active, h.Engine.GetSnapshot().State);
    }

    // ---------------- 待机模式（PS5 式） ----------------

    [Fact]
    public void RestMode_Enable_SetsAwayModeAndPersists()
    {
        using var h = CreateHarness();

        var result = h.Engine.EnableRestMode("test");

        Assert.True(result.Success);
        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.True(h.Power.AwayMode);
        Assert.True(h.Sink.SettingsChanges[^1].RestModeArmed);
    }

    [Fact]
    public void RestMode_Disable_ReleasesAwayMode()
    {
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");

        var result = h.Engine.DisableRestMode("test");

        Assert.True(result.Success);
        Assert.False(h.Engine.GetSnapshot().RestModeActive);
        Assert.False(h.Power.AwayMode);
        Assert.False(h.Sink.SettingsChanges[^1].RestModeArmed);
    }

    [Fact]
    public void RestMode_LowBattery_AutoExits()
    {
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");
        h.System.Power = new SystemPowerReading(false, 15, 1);

        h.Engine.Tick();

        Assert.False(h.Engine.GetSnapshot().RestModeActive);
        Assert.False(h.Power.AwayMode);
        Assert.False(h.Sink.SettingsChanges[^1].RestModeArmed);
    }

    [Fact]
    public void RestMode_AcPower_LowBatteryKeepsArmed()
    {
        // 交流供电时低电量不退出待机。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");
        h.System.Power = new SystemPowerReading(true, 10, 1);

        h.Engine.Tick();

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.True(h.Power.AwayMode);
    }

    [Fact]
    public void RestMode_Recovery_KeepsArmed()
    {
        // 产品规则：待机模式只有用户手动关闭（或低电量自动退出），紧急恢复不解除武装。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");

        h.Engine.Recover(RecoveryReason.EmergencyHotkey, RecoverySource.Local, null);

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.True(h.Power.AwayMode);
        Assert.True(h.Engine.GetSettings().RestModeArmed);
    }

    [Fact]
    public void RestMode_ServiceStop_KeepsArmedState()
    {
        // 服务停止同样不动待机状态：持久化武装状态保留，服务重启后自动恢复。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");

        h.Engine.Recover(RecoveryReason.ServiceStop, RecoverySource.Local, null);

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.True(h.Engine.GetSettings().RestModeArmed);
    }

    [Fact]
    public void RestMode_Streaming_PausesAwayMode()
    {
        // 串流期间暂停离开模式（恢复音频端点供 Sunshine 抓取），串流结束恢复。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");
        Assert.True(h.Power.AwayMode);

        h.Engine.SetStreamingActive(true, "streaming");

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.False(h.Power.AwayMode);

        h.Engine.SetStreamingActive(false, "streaming");

        Assert.True(h.Power.AwayMode);
    }

    [Fact]
    public void RestMode_SessionEnd_ReleasesAwayModeButKeepsArmed()
    {
        // 用户唤醒进桌面：Away Mode 释放（离开模式静音音频），武装保持（下次睡眠仍进待机）。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");

        h.Engine.SetRestSessionActive(false, "rest-session");

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.False(h.Power.AwayMode);
        Assert.True(h.Engine.GetSettings().RestModeArmed);

        h.Engine.SetRestSessionActive(true, "rest-session");

        Assert.True(h.Power.AwayMode);
    }

    [Fact]
    public void RestMode_SessionEndWhileStreaming_StaysPausedUntilSessionResumes()
    {
        // 串流中唤醒（会话结束）：音频仍需保持可用（串流还没断），Away Mode 不恢复；
        // 串流结束后会话仍未恢复，也不持有；重新进入会话才恢复。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("test");
        h.Engine.SetStreamingActive(true, "streaming");
        Assert.False(h.Power.AwayMode);

        h.Engine.SetRestSessionActive(false, "rest-session");
        h.Engine.SetStreamingActive(false, "streaming");
        Assert.False(h.Power.AwayMode);

        h.Engine.SetRestSessionActive(true, "rest-session");
        Assert.True(h.Power.AwayMode);
    }

    [Fact]
    public void RestMode_ArmedWhileStreaming_DoesNotOpenAwayMode()
    {
        // 未武装期间客户端同样会上报串流。开启待机时必须按这个事实先暂停离开模式，
        // 否则会在串流中静音音频端点（实测：Sunshine 音频流建立失败）。
        using var h = CreateHarness();
        h.Engine.SetStreamingActive(true, "streaming");

        h.Engine.EnableRestMode("test");

        Assert.False(h.Power.AwayMode);

        // 客户端进入武装后 1 秒内重报真值，串流结束即恢复离开模式。
        h.Engine.SetStreamingActive(false, "streaming");
        Assert.True(h.Power.AwayMode);
    }

    [Fact]
    public void RestMode_StartupRestore_ArmsWithoutHoldingAwayMode()
    {
        // 开机恢复只恢复"武装"：会话不在进行中就不该持有离开模式（实测持续持有会让桌面无声）。
        using var h = CreateHarness();
        h.Engine.EnableRestMode("service-startup", enterSession: false);

        Assert.True(h.Engine.GetSnapshot().RestModeActive);
        Assert.False(h.Power.AwayMode);

        // 用户真的进入待机时才持有离开模式。
        h.Engine.SetRestSessionActive(true, "rest-session");
        Assert.True(h.Power.AwayMode);
    }

    // ---------------- 电源策略 ----------------

    [Fact]
    public void AcLost_SuspendsPowerRequest_WhenOnlyOnAc()
    {
        using var h = CreateHarness(new GuardianSettings { OnlyOnAcPower = true });
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        Assert.True(h.Power.IsActive);

        h.System.Power = new SystemPowerReading(false, 80, 1);
        h.Engine.Tick();

        var snapshot = h.Engine.GetSnapshot();
        Assert.False(h.Power.IsActive);
        Assert.True(snapshot.PowerRequestSuspended);
        Assert.Equal(ProtectionState.Active, snapshot.State); // 其他保护继续
    }

    [Fact]
    public void AcBack_ResumesPowerRequest()
    {
        using var h = CreateHarness(new GuardianSettings { OnlyOnAcPower = true });
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);
        h.System.Power = new SystemPowerReading(false, 80, 1);
        h.Engine.Tick();
        Assert.False(h.Power.IsActive);

        h.System.Power = new SystemPowerReading(true, null, 1);
        h.Engine.Tick();

        Assert.True(h.Power.IsActive);
        Assert.False(h.Engine.GetSnapshot().PowerRequestSuspended);
    }

    [Fact]
    public void ManualMaxDuration_AutoDisablesManualProtection()
    {
        using var h = CreateHarness(new GuardianSettings { MaxManualProtectionMinutes = 30 });
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        h.Engine.Tick();

        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.False(h.Power.IsActive);
    }

    // ---------------- 启动恢复 ----------------

    [Fact]
    public void StartupRestore_ReleasesAllAdapters()
    {
        using var h = CreateHarness();
        h.SetDisplayPrivacyValidated(true);
        h.Engine.EnableProtection(ProtectionMode.Privacy, "test", true);

        h.Engine.StartupRestore();

        Assert.Equal(ProtectionState.Inactive, h.Engine.GetSnapshot().State);
        Assert.False(h.Display.IsActive);
        Assert.False(h.Power.IsActive);
        Assert.Contains(h.Sink.Recoveries, r => r.Reason == RecoveryReason.StartupRestore);
    }

    [Fact]
    public void ResumeAfterRestart_ReappliesFromPersistedSessions()
    {
        using var h = CreateHarness(new GuardianSettings { ResumePolicy = ResumePolicy.ResumeLast });
        h.Engine.LoadPersistedSessions(new[]
        {
            new SessionInfo
            {
                Id = Guid.NewGuid(),
                Name = "恢复的会话",
                Status = SessionStatus.Active,
                RequiredMode = ProtectionMode.Privacy,
                CreatedAt = h.Clock.UtcNow.ToLocalTime(),
            },
        });
        h.Engine.StartupRestore();

        h.Engine.ResumeAfterRestart();

        Assert.Equal(ProtectionState.Active, h.Engine.GetSnapshot().State);
        Assert.True(h.Display.IsActive);
        Assert.True(h.Power.IsActive);
    }

    // ---------------- 设置 ----------------

    [Fact]
    public void UpdateSettings_ClampsInvalidValues()
    {
        using var h = CreateHarness();

        var result = h.Engine.UpdateSettings(new GuardianSettings
        {
            ClientLostThresholdSeconds = 1, // 低于下限
            MaxManualProtectionMinutes = 99999, // 高于上限
        });

        Assert.True(result.Success);
        var stored = h.Engine.GetSettings();
        Assert.Equal(5, stored.ClientLostThresholdSeconds);
        Assert.Equal(10080, stored.MaxManualProtectionMinutes);
        Assert.Single(h.Sink.SettingsChanges);
    }

    // ---------------- 工具 ----------------

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
}
