using Guardian.Contracts;
using Guardian.Core.Engine;
using Xunit;

namespace Guardian.Core.Tests.Engine;

public class PolicyAggregatorTests
{
    [Fact]
    public void NothingActive_None()
    {
        var policy = PolicyAggregator.Aggregate(ProtectionMode.None, false, Array.Empty<WorkSession>());
        Assert.Equal(DesiredPolicy.None, policy);
    }

    [Fact]
    public void ManualOnly_UsesManualMode()
    {
        var policy = PolicyAggregator.Aggregate(ProtectionMode.InputIsolation, true, Array.Empty<WorkSession>());
        Assert.Equal(ProtectionMode.InputIsolation, policy.Mode);
        Assert.True(policy.SystemRequired);
        Assert.True(policy.InputIsolation);
        Assert.True(policy.DisplayPrivacy);
    }

    [Fact]
    public void SessionsOnly_MaxModeWins()
    {
        var sessions = new[]
        {
            new WorkSession(new Contracts.Models.SessionInfo { Name = "a", RequiredMode = ProtectionMode.Privacy }),
            new WorkSession(new Contracts.Models.SessionInfo { Name = "b", RequiredMode = ProtectionMode.InputIsolation }),
        };
        var policy = PolicyAggregator.Aggregate(ProtectionMode.None, false, sessions);
        Assert.Equal(ProtectionMode.InputIsolation, policy.Mode);
        Assert.True(policy.SystemRequired);
    }

    [Fact]
    public void ManualOff_ButSessionsActive_StillProtected()
    {
        var sessions = new[]
        {
            new WorkSession(new Contracts.Models.SessionInfo { Name = "a", RequiredMode = ProtectionMode.Privacy }),
        };
        var policy = PolicyAggregator.Aggregate(ProtectionMode.None, false, sessions);
        Assert.True(policy.SystemRequired);
        Assert.Equal(ProtectionMode.Privacy, policy.Mode);
    }
}

public class SessionManagerTests
{
    [Fact]
    public void End_MarksSessionEnded()
    {
        var manager = new SessionManager();
        var session = manager.Create(new Contracts.Models.SessionInfo { Id = Guid.NewGuid(), Name = "x" });
        var now = DateTimeOffset.UtcNow;

        var ok = manager.End(session.Info.Id, SessionEndReason.UserRequest, now, out var ended);

        Assert.True(ok);
        Assert.Equal(SessionStatus.Ended, ended!.Info.Status);
        Assert.Equal(SessionEndReason.UserRequest, ended.Info.EndReason);
        Assert.Empty(manager.GetActive());
    }

    [Fact]
    public void End_UnknownId_ReturnsFalse()
    {
        var manager = new SessionManager();
        Assert.False(manager.End(Guid.NewGuid(), SessionEndReason.UserRequest, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void ExpireDue_EndsOnlyExpiredSessions()
    {
        var manager = new SessionManager();
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var expiring = manager.Create(new Contracts.Models.SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "expiring",
            CreatedAt = now.AddMinutes(-11).ToLocalTime(),
            MaxDurationMinutes = 10,
        });
        manager.Create(new Contracts.Models.SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "still-ok",
            CreatedAt = now.ToLocalTime(),
            MaxDurationMinutes = 10,
        });

        var due = manager.ExpireDue(now);

        Assert.Single(due);
        Assert.Equal(expiring.Info.Id, due[0].Info.Id);
        Assert.Equal(SessionEndReason.Timeout, due[0].Info.EndReason);
        Assert.Single(manager.GetActive());
    }

    [Fact]
    public void EndAll_EndsAllActive()
    {
        var manager = new SessionManager();
        manager.Create(new Contracts.Models.SessionInfo { Id = Guid.NewGuid(), Name = "a" });
        manager.Create(new Contracts.Models.SessionInfo { Id = Guid.NewGuid(), Name = "b" });

        var ended = manager.EndAll(SessionEndReason.EmergencyRecovery, DateTimeOffset.UtcNow);

        Assert.Equal(2, ended.Count);
        Assert.Empty(manager.GetActive());
    }
}

public class RecoveryCoordinatorTests
{
    [Fact]
    public void Execute_OrdersSteps_InputFirst()
    {
        var calls = new List<string>();
        var recorderInput = new RecordingInput(calls);
        var recorderDisplay = new RecordingDisplay(calls);
        var recorderPower = new RecordingPower(calls);
        var coordinator2 = new RecoveryCoordinator(recorderDisplay, recorderPower, recorderInput);
        var clock = new TestDoubles.FakeClock();

        var record = coordinator2.Execute(RecoveryReason.EmergencyHotkey, RecoverySource.Local, null, clock);

        Assert.Equal(new[] { "input", "display", "power" }, calls);
        Assert.True(record.Success);
        Assert.Equal(3, record.Steps.Count);
    }

    [Fact]
    public void Execute_PartialFailure_ContinuesRemainingSteps()
    {
        var calls = new List<string>();
        var input = new RecordingInput(calls);
        var display = new ThrowingDisplay(calls);
        var power = new RecordingPower(calls);
        var coordinator = new RecoveryCoordinator(display, power, input);
        var clock = new TestDoubles.FakeClock();

        var record = coordinator.Execute(RecoveryReason.UserRequest, RecoverySource.Local, null, clock);

        Assert.Equal(new[] { "input", "display", "power" }, calls); // display 失败后 power 仍执行
        Assert.False(record.Success);
        Assert.False(record.Steps.Single(s => s.Step == "restore-display").Success);
    }

    private sealed class RecordingInput : Guardian.Core.Ports.IInputIsolationController
    {
        private readonly List<string> _calls;
        public RecordingInput(List<string> calls) => _calls = calls;
        public bool IsSupported => true;
        public bool IsActive => false;
        public void Enable(Action onEmergency, string emergencyHotkeyKey) { }
        public void Disable() => _calls.Add("input");
    }

    private sealed class RecordingDisplay : Guardian.Core.Ports.IDisplayPrivacyController
    {
        private readonly List<string> _calls;
        public RecordingDisplay(List<string> calls) => _calls = calls;
        public bool IsActive => false;
        public void Enable() { }
        public void Disable() => _calls.Add("display");
    }

    private sealed class ThrowingDisplay : Guardian.Core.Ports.IDisplayPrivacyController
    {
        private readonly List<string> _calls;
        public ThrowingDisplay(List<string> calls) => _calls = calls;
        public bool IsActive => false;
        public void Enable() { }
        public void Disable()
        {
            _calls.Add("display");
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class RecordingPower : Guardian.Core.Ports.IPowerRequestController
    {
        private readonly List<string> _calls;
        public RecordingPower(List<string> calls) => _calls = calls;
        public bool IsActive => false;
        public void Apply(bool keepDisplayOn) { }
        public void Release() => _calls.Add("power");
        public void SetAwayMode(bool enabled) { }
    }
}
