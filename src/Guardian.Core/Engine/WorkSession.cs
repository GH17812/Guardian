using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.Core.Engine;

/// <summary>工作会话领域对象。</summary>
public sealed class WorkSession
{
    public WorkSession(SessionInfo info) => Info = info;

    public SessionInfo Info { get; }

    public DateTimeOffset ExpiresAt
    {
        get
        {
            if (Info.MaxDurationMinutes is not { } m || m <= 0)
            {
                return DateTimeOffset.MaxValue;
            }
            return Info.CreatedAt.AddMinutes(m);
        }
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void End(SessionEndReason reason, DateTimeOffset now)
    {
        if (Info.Status != SessionStatus.Active)
        {
            return;
        }
        Info.Status = SessionStatus.Ended;
        Info.EndedAt = now;
        Info.EndReason = reason;
    }
}
