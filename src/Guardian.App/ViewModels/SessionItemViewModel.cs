using Guardian.App.Services;
using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.App.ViewModels;

/// <summary>工作会话列表项视图模型（展示用只读信息）。</summary>
public sealed class SessionItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    public string WorkloadTypeText { get; }
    public string RequiredModeText { get; }
    public string CreatedAtText { get; }
    public string StatusText { get; }

    public SessionItemViewModel(SessionInfo info, LocalizationManager localization)
    {
        Id = info.Id;
        Name = info.Name;
        WorkloadTypeText = LocalizeWorkload(info.WorkloadType, localization);
        RequiredModeText = LocalizeMode(info.RequiredMode, localization);
        CreatedAtText = info.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        StatusText = info.Status == SessionStatus.Active
            ? localization.L("Str.SessionActive")
            : localization.L("Str.SessionEnded");
    }

    private static string LocalizeMode(ProtectionMode mode, LocalizationManager l) => mode switch
    {
        ProtectionMode.Privacy => l.L("Str.ModePrivacy"),
        ProtectionMode.InputIsolation => l.L("Str.ModeInputIsolation"),
        ProtectionMode.EnhancedIsolation => l.L("Str.ModeEnhancedIsolation"),
        _ => l.L("Str.ModeNone"),
    };

    private static string LocalizeWorkload(WorkloadType type, LocalizationManager l) => type switch
    {
        WorkloadType.RemoteDesktop => l.L("Str.WorkloadRemoteDesktop"),
        WorkloadType.AiAgent => l.L("Str.WorkloadAiAgent"),
        WorkloadType.RpaAutomation => l.L("Str.WorkloadRpaAutomation"),
        WorkloadType.Compilation => l.L("Str.WorkloadCompilation"),
        WorkloadType.Rendering => l.L("Str.WorkloadRendering"),
        WorkloadType.LocalInference => l.L("Str.WorkloadLocalInference"),
        _ => l.L("Str.WorkloadOther"),
    };
}
