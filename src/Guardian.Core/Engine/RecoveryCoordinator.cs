using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Ports;

namespace Guardian.Core.Engine;

/// <summary>
/// 恢复协调器：按固定顺序执行恢复步骤，单步失败不影响后续步骤。
/// 顺序原则：先恢复控制权（解除输入隔离），再恢复显示，最后释放电源请求。
/// </summary>
public sealed class RecoveryCoordinator
{
    private readonly IDisplayPrivacyController _display;
    private readonly IPowerRequestController _power;
    private readonly IInputIsolationController _input;

    public RecoveryCoordinator(IDisplayPrivacyController display, IPowerRequestController power, IInputIsolationController input)
    {
        _display = display;
        _power = power;
        _input = input;
    }

    public RecoveryRecord Execute(RecoveryReason reason, RecoverySource source, string? detail, IClock clock)
    {
        var record = new RecoveryRecord
        {
            Timestamp = clock.UtcNow.ToLocalTime(),
            Reason = reason,
            Source = source,
            Detail = detail,
        };

        var steps = new (string Name, Action Action)[]
        {
            ("unblock-input", () => _input.Disable()),
            ("restore-display", () => _display.Disable()),
            ("release-power", () => _power.Release()),
        };

        foreach (var (name, action) in steps)
        {
            try
            {
                action();
                record.Steps.Add(new RecoveryStepRecord { Step = name, Success = true });
            }
            catch (Exception ex)
            {
                record.Steps.Add(new RecoveryStepRecord { Step = name, Success = false, Error = ex.Message });
            }
        }

        record.Success = record.Steps.All(s => s.Success);
        return record;
    }
}
