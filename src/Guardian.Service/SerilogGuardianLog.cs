using Guardian.Contracts;
using Guardian.Core.Ports;
using Guardian.Storage.Repositories;
using Serilog;
using Serilog.Events;

namespace Guardian.Service;

/// <summary>
/// 引擎日志出口：写入 Serilog（文件/控制台）与 SQLite 操作日志表。
/// 任何异常都必须被吞掉 —— 日志通道不得反噬保护引擎。
/// </summary>
public sealed class SerilogGuardianLog : IGuardianLog
{
    private readonly LogRepository _logRepository;

    public SerilogGuardianLog(LogRepository logRepository) => _logRepository = logRepository;

    public void Write(GuardianLogLevel level, string category, string message, string? detail = null)
    {
        try
        {
            var serilogLevel = level switch
            {
                GuardianLogLevel.Trace => LogEventLevel.Verbose,
                GuardianLogLevel.Debug => LogEventLevel.Debug,
                GuardianLogLevel.Information => LogEventLevel.Information,
                GuardianLogLevel.Warning => LogEventLevel.Warning,
                GuardianLogLevel.Error => LogEventLevel.Error,
                _ => LogEventLevel.Fatal,
            };
            var logger = Log.ForContext("Category", category);
            logger.Write(serilogLevel, detail is null ? "{Message}" : "{Message} | {Detail}", message, detail);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _logRepository.Add(level, category, message, detail);
        }
        catch
        {
            // 数据库不可用时仅保留文件日志。
        }
    }
}
