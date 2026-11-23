using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.Core.Engine;

/// <summary>引擎命令执行结果。</summary>
public readonly record struct EngineResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static EngineResult Ok() => new(true);
    public static EngineResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>服务端设置持有者（线程安全，带取值范围收敛）。</summary>
public sealed class SettingsHolder
{
    private readonly object _sync = new();
    private GuardianSettings _current;

    public SettingsHolder(GuardianSettings initial) => _current = initial;

    public GuardianSettings Get()
    {
        lock (_sync)
        {
            return _current.Clone();
        }
    }

    public void Update(GuardianSettings incoming)
    {
        lock (_sync)
        {
            // 取值范围收敛，拒绝非法配置。
            incoming.ClientLostThresholdSeconds = Math.Clamp(incoming.ClientLostThresholdSeconds, 5, 300);
            incoming.MaxManualProtectionMinutes = Math.Clamp(incoming.MaxManualProtectionMinutes, 0, 10080);
            _current = incoming.Clone();
        }
    }
}
