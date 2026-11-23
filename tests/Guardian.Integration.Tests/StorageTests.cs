using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Storage;
using Guardian.Storage.Repositories;
using Xunit;

namespace Guardian.Integration.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "guardian-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GuardianDb _db;

    public StorageTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new GuardianDb(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Settings_RoundTrips()
    {
        var repo = new SettingsRepository(_db);
        var settings = new GuardianSettings { EmergencyHotkeyKey = "F10", UiLossAction = UiLossAction.KeepProtection };

        repo.SaveGuardianSettings(settings);
        var loaded = repo.LoadGuardianSettings(new GuardianSettings());

        Assert.Equal("F10", loaded.EmergencyHotkeyKey);
        Assert.Equal(UiLossAction.KeepProtection, loaded.UiLossAction);
    }

    [Fact]
    public void Settings_MissingKey_ReturnsFallback()
    {
        var repo = new SettingsRepository(_db);
        var fallback = new GuardianSettings { EmergencyHotkeyKey = "F9" };

        var loaded = repo.LoadGuardianSettings(fallback);

        Assert.Equal("F9", loaded.EmergencyHotkeyKey);
    }

    [Fact]
    public void Sessions_UpsertAndLoad()
    {
        var repo = new SessionRepository(_db);
        var session = new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "会话A",
            WorkloadType = WorkloadType.AiAgent,
            RequiredMode = ProtectionMode.InputIsolation,
            Status = SessionStatus.Active,
            CreatedAt = DateTimeOffset.Now,
            MaxDurationMinutes = 30,
        };

        repo.Upsert(session);
        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTimeOffset.Now;
        session.EndReason = SessionEndReason.UserRequest;
        repo.Upsert(session);

        var all = repo.GetAll();
        Assert.Single(all);
        Assert.Equal(SessionStatus.Ended, all[0].Status);
        Assert.Equal(SessionEndReason.UserRequest, all[0].EndReason);
        Assert.Equal(WorkloadType.AiAgent, all[0].WorkloadType);
        Assert.Empty(repo.GetActive());
    }

    [Fact]
    public void Logs_AddAndRetrieve()
    {
        var repo = new LogRepository(_db);
        repo.Add(GuardianLogLevel.Information, "test", "hello", null);
        repo.Add(GuardianLogLevel.Error, "test", "boom", "detail-x");

        var recent = repo.GetRecent(100);

        Assert.Equal(2, recent.Count);
        Assert.Equal("boom", recent[1].Message);
        Assert.Equal("detail-x", recent[1].Detail);
    }

    [Fact]
    public void Recovery_AddAndRetrieve()
    {
        var repo = new RecoveryRepository(_db);
        var record = new RecoveryRecord
        {
            Timestamp = DateTimeOffset.Now,
            Reason = RecoveryReason.EmergencyHotkey,
            Source = RecoverySource.Local,
            Success = true,
            Steps =
            {
                new RecoveryStepRecord { Step = "unblock-input", Success = true },
            },
            Detail = "test",
        };

        repo.Add(record);
        var recent = repo.GetRecent(10);

        Assert.Single(recent);
        Assert.Equal(RecoveryReason.EmergencyHotkey, recent[0].Reason);
        Assert.Single(recent[0].Steps);
    }

    [Fact]
    public void Capability_SaveAndLoadLatest()
    {
        var repo = new CapabilityRepository(_db);
        var report = new CapabilityReport
        {
            GeneratedAt = DateTimeOffset.Now,
            DisplayPrivacyValidated = true,
            InputDevices = { new InputDeviceCapability { DeviceType = InputDeviceType.Keyboard, Count = 2 } },
        };

        repo.Save(report);
        var loaded = repo.GetLatest();

        Assert.NotNull(loaded);
        Assert.True(loaded!.DisplayPrivacyValidated);
        Assert.Single(loaded.InputDevices);
    }

    [Fact]
    public void Db_OpenTwice_PreservesData()
    {
        var path = Path.Combine(_dir, "persist.db");
        var db1 = new GuardianDb(path);
        new SettingsRepository(db1).SetString("k", "v");
        db1.Dispose();

        var db2 = new GuardianDb(path);
        var value = new SettingsRepository(db2).GetString("k");
        db2.Dispose();

        Assert.Equal("v", value);
    }
}
