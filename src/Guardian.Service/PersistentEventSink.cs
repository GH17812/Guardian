using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Ports;
using Guardian.Storage.Repositories;

namespace Guardian.Service;

/// <summary>
/// 引擎事件持久化出口（SQLite）。
/// 实现契约：快速返回、自身不抛异常、绝不阻塞保护引擎。
/// </summary>
public sealed class PersistentEventSink : IGuardianEventSink
{
    private readonly IGuardianLog _log;
    private readonly SessionRepository _sessions;
    private readonly SettingsRepository _settings;
    private readonly RecoveryRepository _recoveries;
    private readonly CapabilityRepository _capabilities;

    public PersistentEventSink(
        IGuardianLog log,
        SessionRepository sessions,
        SettingsRepository settings,
        RecoveryRepository recoveries,
        CapabilityRepository capabilities)
    {
        _log = log;
        _sessions = sessions;
        _settings = settings;
        _recoveries = recoveries;
        _capabilities = capabilities;
    }

    public void OnLog(GuardianLogLevel level, string category, string message, string? detail = null)
        => _log.Write(level, category, message, detail);

    public void OnRecovery(RecoveryRecord record)
    {
        try { _recoveries.Add(record); }
        catch (Exception ex) { _log.Write(GuardianLogLevel.Error, "storage", "保存恢复记录失败", ex.Message); }
    }

    public void OnSessionChanged(SessionInfo session)
    {
        try { _sessions.Upsert(session); }
        catch (Exception ex) { _log.Write(GuardianLogLevel.Error, "storage", "保存会话失败", ex.Message); }
    }

    public void OnSettingsChanged(GuardianSettings settings)
    {
        try { _settings.SaveGuardianSettings(settings); }
        catch (Exception ex) { _log.Write(GuardianLogLevel.Error, "storage", "保存设置失败", ex.Message); }
    }

    public void OnCapabilityChanged(CapabilityReport report)
    {
        try { _capabilities.Save(report); }
        catch (Exception ex) { _log.Write(GuardianLogLevel.Error, "storage", "保存能力报告失败", ex.Message); }
    }
}
