using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.Core.Engine;

/// <summary>工作会话管理：创建、结束、超时检查。</summary>
public sealed class SessionManager
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, WorkSession> _sessions = new();

    public WorkSession Create(SessionInfo info)
    {
        lock (_sync)
        {
            var session = new WorkSession(info);
            _sessions[info.Id] = session;
            return session;
        }
    }

    public WorkSession? Get(Guid id)
    {
        lock (_sync)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    public bool End(Guid id, SessionEndReason reason, DateTimeOffset now, out WorkSession? session)
    {
        lock (_sync)
        {
            session = _sessions.GetValueOrDefault(id);
            if (session is null)
            {
                return false;
            }
            session.End(reason, now);
            return session.Info.Status == SessionStatus.Ended;
        }
    }

    public List<WorkSession> EndAll(SessionEndReason reason, DateTimeOffset now)
    {
        lock (_sync)
        {
            var result = new List<WorkSession>();
            foreach (var s in _sessions.Values)
            {
                if (s.Info.Status == SessionStatus.Active)
                {
                    s.End(reason, now);
                    result.Add(s);
                }
            }
            return result;
        }
    }

    public List<WorkSession> GetActive()
    {
        lock (_sync)
        {
            return _sessions.Values.Where(s => s.Info.Status == SessionStatus.Active).ToList();
        }
    }

    public List<WorkSession> GetAll()
    {
        lock (_sync)
        {
            return _sessions.Values.ToList();
        }
    }

    public List<WorkSession> ExpireDue(DateTimeOffset now)
    {
        lock (_sync)
        {
            var due = _sessions.Values
                .Where(s => s.Info.Status == SessionStatus.Active && s.IsExpired(now))
                .ToList();
            foreach (var s in due)
            {
                s.End(SessionEndReason.Timeout, now);
            }
            return due;
        }
    }

    /// <summary>载入持久化的会话（服务重启恢复场景）。</summary>
    public void Load(IEnumerable<SessionInfo> infos)
    {
        lock (_sync)
        {
            foreach (var info in infos)
            {
                _sessions[info.Id] = new WorkSession(info);
            }
        }
    }
}
