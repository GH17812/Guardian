using Guardian.Contracts;
using Guardian.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Guardian.Storage.Repositories;

/// <summary>操作日志仓储。</summary>
public sealed class LogRepository
{
    private const int MaxRows = 100_000;

    private readonly GuardianDb _db;

    public LogRepository(GuardianDb db) => _db = db;

    public long Add(GuardianLogLevel level, string category, string message, string? detail)
        => _db.QueryScalar<long>("""
            INSERT INTO operation_logs (ts, level, category, message, detail)
            VALUES ($ts, $level, $cat, $msg, $detail);
            SELECT last_insert_rowid();
            """,
            new SqliteParameter("$ts", DateTimeOffset.Now.ToString("o")),
            new SqliteParameter("$level", level.ToString()),
            new SqliteParameter("$cat", category),
            new SqliteParameter("$msg", message),
            new SqliteParameter("$detail", detail));

    public List<OperationLogEntry> GetRecent(int maxCount)
    {
        var limit = Math.Clamp(maxCount, 1, 5000);
        return _db.Query($"""
            SELECT * FROM (SELECT * FROM operation_logs ORDER BY id DESC LIMIT $limit) ORDER BY id;
            """, Map, new SqliteParameter("$limit", limit));
    }

    public void Trim()
        => _db.Execute($"DELETE FROM operation_logs WHERE id < (SELECT MAX(id) - {MaxRows} FROM operation_logs);");

    private static OperationLogEntry Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Timestamp = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts"))),
        Level = Enum.Parse<GuardianLogLevel>(r.GetString(r.GetOrdinal("level"))),
        Category = r.GetString(r.GetOrdinal("category")),
        Message = r.GetString(r.GetOrdinal("message")),
        Detail = r.IsDBNull(r.GetOrdinal("detail")) ? null : r.GetString(r.GetOrdinal("detail")),
    };
}
