using Guardian.Contracts;

namespace Guardian.Core.Engine;

/// <summary>由手动保护与全部活动会话聚合出的目标策略。</summary>
public readonly record struct DesiredPolicy(ProtectionMode Mode, bool SystemRequired)
{
    public bool DisplayPrivacy => Mode >= ProtectionMode.Privacy;
    public bool InputIsolation => Mode >= ProtectionMode.InputIsolation;

    public static readonly DesiredPolicy None = new(ProtectionMode.None, false);
}

/// <summary>策略聚合器：手动保护 + 各会话所需能力的并集。</summary>
public static class PolicyAggregator
{
    /// <summary>聚合规则：生效模式取手动与所有会话中的最高模式；只要有任一来源有效则保持系统运行。</summary>
    public static DesiredPolicy Aggregate(ProtectionMode manualMode, bool manualEnabled, IEnumerable<WorkSession> activeSessions)
    {
        var mode = manualEnabled ? manualMode : ProtectionMode.None;
        var any = manualEnabled;

        foreach (var s in activeSessions)
        {
            if (s.Info.RequiredMode > mode)
            {
                mode = s.Info.RequiredMode;
            }
            any = true;
        }

        return any ? new DesiredPolicy(mode, true) : DesiredPolicy.None;
    }
}
