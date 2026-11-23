namespace Guardian.Contracts.Models;

/// <summary>输入设备能力条目。</summary>
public sealed class InputDeviceCapability
{
    public InputDeviceType DeviceType { get; set; }
    public int Count { get; set; }
}

/// <summary>系统能力检测报告。</summary>
public sealed class CapabilityReport
{
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>输入设备清单。</summary>
    public List<InputDeviceCapability> InputDevices { get; set; } = new();

    /// <summary>远程输入区分能力。</summary>
    public RemoteInputDetectionCapability RemoteInputDetection { get; set; } = RemoteInputDetectionCapability.Unknown;

    /// <summary>远程输入区分的说明（含已知限制）。</summary>
    public string? RemoteInputDetectionNote { get; set; }

    /// <summary>显示隐私（关闭显示器）是否经过远程捕获验证：null=未验证，true=正常，false=异常。</summary>
    public bool? DisplayPrivacyValidated { get; set; }

    public DateTimeOffset? DisplayPrivacyValidatedAt { get; set; }

    /// <summary>输入隔离实验模式是否可用（Phase 1 恒为 true，但默认关闭、需双重确认）。</summary>
    public bool InputIsolationExperimentalAvailable { get; set; } = true;

    /// <summary>增强隔离（Phase 2）是否可用。</summary>
    public bool EnhancedIsolationAvailable { get; set; }

    /// <summary>其他说明。</summary>
    public List<string> Notes { get; set; } = new();
}
