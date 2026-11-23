using Microsoft.Data.Sqlite;

namespace Guardian.Storage;

/// <summary>
/// SQLite 数据库封装：单连接 + WAL + 锁串行化。
/// 隐私承诺：不保存屏幕内容、键盘输入内容或 AI 对话内容。
/// </summary>
public sealed class GuardianDb : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly object _sync = new();

    public GuardianDb(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    public string DatabasePath => _connection.DataSource;

    private void Initialize()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute($"""
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaVersion});

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                workload_type TEXT NOT NULL,
                status TEXT NOT NULL,
                required_mode TEXT NOT NULL,
                source TEXT NOT NULL,
                allow_auto_end INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                ended_at TEXT,
                end_reason TEXT,
                max_duration_minutes INTEGER,
                source_detail TEXT
            );

            CREATE TABLE IF NOT EXISTS operation_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                level TEXT NOT NULL,
                category TEXT NOT NULL,
                message TEXT NOT NULL,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_logs_ts ON operation_logs (ts);

            CREATE TABLE IF NOT EXISTS recovery_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                reason TEXT NOT NULL,
                source TEXT NOT NULL,
                success INTEGER NOT NULL,
                steps TEXT NOT NULL,
                detail TEXT
            );

            CREATE TABLE IF NOT EXISTS capability_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            """);
    }

    public void Execute(string sql, params SqliteParameter[] parameters)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(Normalize(parameters));
            cmd.ExecuteNonQuery();
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        lock (_sync)
        {
            var result = new List<T>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(Normalize(parameters));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(map(reader));
            }
            return result;
        }
    }

    public T? QueryScalar<T>(string sql, params SqliteParameter[] parameters)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(Normalize(parameters));
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
        }
    }

    /// <summary>Microsoft.Data.Sqlite 要求 null 值必须显式绑定，统一转换为 DBNull。</summary>
    private static SqliteParameter[] Normalize(SqliteParameter[] parameters)
    {
        foreach (var p in parameters)
        {
            if (p.Value is null)
            {
                p.Value = DBNull.Value;
            }
        }
        return parameters;
    }

    public void Dispose() => _connection.Dispose();
}
