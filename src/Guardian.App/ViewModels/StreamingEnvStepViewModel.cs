using System.Windows.Media;

namespace Guardian.App.ViewModels;

/// <summary>串流环境分步记录的一行：步骤名、结论、取证文字。证据原文来自服务端探测，不做美化改写。</summary>
public sealed class StreamingEnvStepViewModel
{
    public StreamingEnvStepViewModel(string title, string outcome, Brush outcomeBrush, string evidence, DateTimeOffset at)
    {
        Title = title;
        Outcome = outcome;
        OutcomeBrush = outcomeBrush;
        Evidence = evidence;
        TimeText = at.ToString("HH:mm:ss");
    }

    public string Title { get; }

    public string Outcome { get; }

    public Brush OutcomeBrush { get; }

    public string Evidence { get; }

    public string TimeText { get; }
}
