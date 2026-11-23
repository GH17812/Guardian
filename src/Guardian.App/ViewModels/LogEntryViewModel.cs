using System.Windows;
using System.Windows.Media;
using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.App.ViewModels;

/// <summary>操作日志条目视图模型（含级别着色）。</summary>
public sealed class LogEntryViewModel
{
    public string TimestampText { get; }
    public string LevelText { get; }
    public string Category { get; }
    public string Message { get; }
    public Brush LevelBrush { get; }

    public LogEntryViewModel(OperationLogEntry entry)
    {
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        LevelText = entry.Level.ToString();
        Category = entry.Category;
        Message = entry.Message;
        LevelBrush = entry.Level switch
        {
            GuardianLogLevel.Error or GuardianLogLevel.Critical => Resolve("DangerBrush"),
            GuardianLogLevel.Warning => Resolve("WarningBrush"),
            GuardianLogLevel.Debug or GuardianLogLevel.Trace => Resolve("DisabledTextBrush"),
            _ => Resolve("PrimaryTextBrush"),
        };
    }

    private static Brush Resolve(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
