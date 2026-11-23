using Guardian.Contracts;
using Guardian.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Guardian.Storage.Repositories;

/// <summary>工作会话仓储。</summary>
public sealed class SessionRepository
{
    private readonly GuardianDb _db;

    public SessionRepository(GuardianDb db) => _db = db;

    public void Upsert(SessionInfo s)
        => _db.Execute("""
            INSERT INTO sessions (id, name, workload_type, status, required_mode, source, allow_auto_end,
                                  created_at, ended_at, end_reason, max_duration_minutes, source_detail)
            VALUES ($id, $name, $wt, $status, $mode, $source, $aae, $created, $ended, $reason, $maxmin, $detail)
            ON CONFLICT(id) DO UPDATE SET
                name = $name, workload_type = $wt, status = $status, required_mode = $mode,
                source = $source, allow_auto_end = $aae, ended_at = $ended, end_reason = $reason,
                max_duration_minutes = $maxmin, source_detail = $detail;
            """,
            new SqliteParameter("$id", s.Id.ToString()),
            new SqliteParameter("$name", s.Name),
            new SqliteParameter("$wt", s.WorkloadType.ToString()),
            new SqliteParameter("$status", s.Status.ToString()),
            new SqliteParameter("$mode", s.RequiredMode.ToString()),
            new SqliteParameter("$source", s.Source.ToString()),
            new SqliteParameter("$aae", s.AllowAutoEnd ? 1 : 0),
            new SqliteParameter("$created", s.CreatedAt.ToString("o")),
            new SqliteParameter("$ended", s.EndedAt?.ToString("o")),
            new SqliteParameter("$reason", s.EndReason?.ToString()),
            new SqliteParameter("$maxmin", s.MaxDurationMinutes),
            new SqliteParameter("$detail", s.SourceDetail));

    public List<SessionInfo> GetAll()
        => _db.Query("SELECT * FROM sessions ORDER BY created_at;", Map);

    public List<SessionInfo> GetActive()
        => _db.Query("SELECT * FROM sessions WHERE status = 'Active' ORDER BY created_at;", Map);

    private static SessionInfo Map(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Name = r.GetString(r.GetOrdinal("name")),
        WorkloadType = Enum.Parse<WorkloadType>(r.GetString(r.GetOrdinal("workload_type"))),
        Status = Enum.Parse<SessionStatus>(r.GetString(r.GetOrdinal("status"))),
        RequiredMode = Enum.Parse<ProtectionMode>(r.GetString(r.GetOrdinal("required_mode"))),
        Source = Enum.Parse<SessionSource>(r.GetString(r.GetOrdinal("source"))),
        AllowAutoEnd = r.GetInt64(r.GetOrdinal("allow_auto_end")) != 0,
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
        EndedAt = r.IsDBNull(r.GetOrdinal("ended_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ended_at"))),
        EndReason = r.IsDBNull(r.GetOrdinal("end_reason")) ? null : Enum.Parse<SessionEndReason>(r.GetString(r.GetOrdinal("end_reason"))),
        MaxDurationMinutes = r.IsDBNull(r.GetOrdinal("max_duration_minutes")) ? null : (int)r.GetInt64(r.GetOrdinal("max_duration_minutes")),
        SourceDetail = r.IsDBNull(r.GetOrdinal("source_detail")) ? null : r.GetString(r.GetOrdinal("source_detail")),
    };
}
