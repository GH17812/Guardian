using Guardian.Contracts.Models;

namespace Guardian.Core.Engine;

/// <summary>系统能力状态（引擎内）。设备探测部分由服务端采集，显示隐私验证结果随用户确认更新。</summary>
public sealed class CapabilityState
{
    private readonly object _sync = new();
    private CapabilityReport _report = new()
    {
        GeneratedAt = DateTimeOffset.Now,
        Notes = { "能力检测尚未完成。" },
    };

    public CapabilityReport Get()
    {
        lock (_sync)
        {
            return _report;
        }
    }

    public void Initialize(CapabilityReport report)
    {
        lock (_sync)
        {
            _report = report;
        }
    }

    /// <summary>更新显示隐私验证结果（保持设备探测部分不变）。</summary>
    public CapabilityReport SetDisplayPrivacyValidation(bool remoteCaptureOk, string? note, DateTimeOffset at)
    {
        lock (_sync)
        {
            _report.DisplayPrivacyValidated = remoteCaptureOk;
            _report.DisplayPrivacyValidatedAt = at;
            if (!string.IsNullOrWhiteSpace(note))
            {
                _report.Notes.Add($"[{at:yyyy-MM-dd HH:mm:ss}] 显示隐私验证：{note}");
            }
            return _report;
        }
    }

    /// <summary>合并客户端上报的输入设备清单（Session 0 服务无法枚举交互会话设备）。</summary>
    public CapabilityReport MergeInputDevices(IReadOnlyCollection<InputDeviceCapability> devices, DateTimeOffset at)
    {
        lock (_sync)
        {
            if (devices.Count > 0)
            {
                _report.InputDevices = devices.ToList();
            }
            _report.Notes.RemoveAll(n => n.StartsWith("[客户端上报]", StringComparison.Ordinal));
            _report.Notes.Add($"[客户端上报] {at:yyyy-MM-dd HH:mm:ss} 输入设备清单：{string.Join(", ", devices.Select(d => $"{d.DeviceType}:{d.Count}"))}");
            return _report;
        }
    }
}
