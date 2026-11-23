namespace Guardian.Contracts;

/// <summary>保护模式。</summary>
public enum ProtectionMode
{
    /// <summary>无保护。</summary>
    None = 0,

    /// <summary>模式一：隐私保护 —— 关闭物理显示输出、保持系统运行，不隔离输入。</summary>
    Privacy = 1,

    /// <summary>模式二：输入隔离 —— 在隐私保护基础上隔离本地物理键鼠，保留远程注入输入与本地紧急恢复。</summary>
    InputIsolation = 2,

    /// <summary>模式三：增强隔离 —— 第二阶段能力（触摸屏/触控板/手写笔/手柄等 HID 隔离）。</summary>
    EnhancedIsolation = 3,
}

/// <summary>保护引擎状态。</summary>
public enum ProtectionState
{
    /// <summary>未启用保护。</summary>
    Inactive = 0,

    /// <summary>正在启用。</summary>
    Activating = 1,

    /// <summary>保护生效中。</summary>
    Active = 2,

    /// <summary>正在恢复。</summary>
    Recovering = 3,

    /// <summary>适配器执行失败，已尽力恢复。真实系统状态可能未知，界面必须如实呈现。</summary>
    Failed = 4,

    /// <summary>状态未知（例如服务刚重启、尚未完成探测）。</summary>
    Unknown = 5,
}

/// <summary>工作负载类型。</summary>
public enum WorkloadType
{
    RemoteDesktop = 0,
    AiAgent = 1,
    RpaAutomation = 2,
    Compilation = 3,
    Rendering = 4,
    LocalInference = 5,
    Other = 6,
}

/// <summary>工作会话状态。</summary>
public enum SessionStatus
{
    Active = 0,
    Ended = 1,
}

/// <summary>会话来源。</summary>
public enum SessionSource
{
    Manual = 0,
    External = 1,
    AutoDetected = 2,
}

/// <summary>会话结束原因。</summary>
public enum SessionEndReason
{
    UserRequest = 0,
    EmergencyRecovery = 1,
    Timeout = 2,
    ServiceStop = 3,
    ServiceRestart = 4,
    SystemReboot = 5,
    ClientLost = 6,
    Other = 7,
}

/// <summary>恢复触发原因。</summary>
public enum RecoveryReason
{
    UserRequest = 0,
    EmergencyHotkey = 1,
    ClientLost = 2,
    AdapterFailure = 3,
    SessionTimeout = 4,
    ServiceStop = 5,
    StartupRestore = 6,
    Other = 7,
}

/// <summary>恢复来源（本地 / 远程）。</summary>
public enum RecoverySource
{
    Local = 0,
    Remote = 1,
}

/// <summary>客户端（UI）丢失后的动作。</summary>
public enum UiLossAction
{
    /// <summary>自动解除保护（fail-open，默认）。</summary>
    Recover = 0,

    /// <summary>继续维持保护（无人值守场景）。</summary>
    KeepProtection = 1,
}

/// <summary>服务重启后的策略。</summary>
public enum ResumePolicy
{
    /// <summary>重启后一律恢复系统（fail-open，默认）。</summary>
    Restore = 0,

    /// <summary>重启后按持久化的会话记录恢复保护（无人值守场景）。</summary>
    ResumeLast = 1,
}

/// <summary>输入设备类型。</summary>
public enum InputDeviceType
{
    Keyboard = 0,
    Mouse = 1,
    TouchScreen = 2,
    TouchPad = 3,
    Pen = 4,
    GamePad = 5,
    OtherHid = 6,
}

/// <summary>远程输入区分能力。</summary>
public enum RemoteInputDetectionCapability
{
    /// <summary>未验证。</summary>
    Unknown = 0,

    /// <summary>支持（基于低层钩子 INJECTED 标记过滤）。</summary>
    Supported = 1,

    /// <summary>不支持。</summary>
    Unsupported = 2,
}

/// <summary>日志级别。</summary>
public enum GuardianLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}
