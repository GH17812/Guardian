using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.Core.Ports;

/// <summary>核心日志抽象。服务端负责落地到 Serilog 与数据库。</summary>
public interface IGuardianLog
{
    void Write(GuardianLogLevel level, string category, string message, string? detail = null);
}

/// <summary>时钟抽象（可测试）。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>系统电源读数。</summary>
public readonly record struct SystemPowerReading(bool AcOnline, int? BatteryPercent, int MonitorCount);

/// <summary>系统状态读取器（只读探测，不做变更）。</summary>
public interface ISystemStateReader
{
    SystemPowerReading ReadPower();
    bool IsRemoteSession();
    DateTimeOffset GetSystemBootTime();
}

/// <summary>显示隐私控制器：关闭 / 恢复物理显示输出。</summary>
public interface IDisplayPrivacyController
{
    bool IsActive { get; }
    void Enable();
    void Disable();
}

/// <summary>电源请求控制器：基于 Windows 官方执行状态 API，可释放。</summary>
public interface IPowerRequestController
{
    bool IsActive { get; }

    /// <param name="keepDisplayOn">为 false 时允许显示器关闭（配合显示隐私）。</param>
    void Apply(bool keepDisplayOn);
    void Release();

    /// <summary>设置离开模式（Away Mode）请求：睡眠动作转为“屏幕熄灭但系统保持运行”，保证串流可达。</summary>
    void SetAwayMode(bool enabled);
}

/// <summary>输入隔离控制器（Phase 1 实验实现）。</summary>
public interface IInputIsolationController
{
    bool IsSupported { get; }
    bool IsActive { get; }

    /// <summary>
    /// 启用隔离。启用期间检测到紧急恢复快捷键时回调 <paramref name="onEmergency"/>（回调不得阻塞）。
    /// <paramref name="emergencyHotkeyKey"/> 为紧急恢复主键名（如 "F12"）。
    /// </summary>
    void Enable(Action onEmergency, string emergencyHotkeyKey);

    /// <summary>解除隔离（幂等）。</summary>
    void Disable();
}

/// <summary>
/// 保护引擎事件出口：服务端实现此接口以持久化事件（SQLite）并转发到日志系统。
/// 实现必须快速返回且自身不抛异常。
/// </summary>
public interface IGuardianEventSink
{
    void OnLog(GuardianLogLevel level, string category, string message, string? detail = null);
    void OnRecovery(RecoveryRecord record);
    void OnSessionChanged(SessionInfo session);
    void OnSettingsChanged(GuardianSettings settings);
    void OnCapabilityChanged(CapabilityReport report);
}

/// <summary>串流环境单步动作结果。Evidence 必须能回答"这一步看到了什么实测值"。</summary>
public readonly record struct StreamingEnvOutcome(bool Ok, string Evidence);

/// <summary>
/// 串流环境执行端口：探测与单步原子动作。实现方运行于 GuardianService（Session 0，SYSTEM），
/// 因此不得依赖交互会话的可见性；每个方法只做一个动作并保持幂等（重复调用不得叠加副作用）。
/// </summary>
public interface IStreamingEnvironmentPort
{
    /// <summary>只读探测，填入 Sunshine / VirtualDisplay 两侧事实，State 由协调器推导。</summary>
    StreamingEnvironmentProfile Probe();

    /// <summary>确保 Sunshine 可用（离线目录优先，其次官方 URL 下载后校验哈希并静默安装）。</summary>
    Task<StreamingEnvOutcome> EnsureSunshineInstalledAsync(CancellationToken token);

    /// <summary>确保虚拟显示根设备存在（缺驱动时安装驱动并创建设备）。</summary>
    Task<StreamingEnvOutcome> EnsureVirtualDisplayInstalledAsync(CancellationToken token);

    /// <summary>
    /// 确保驱动的建屏设置已落位。这一步必须独立存在：驱动只在设备启动时按该文件创建监视器，
    /// 而"设备已存在/已启用"会让安装与启用两步都跳过 —— 依附在它们里面就永远轮不到放置。
    /// </summary>
    Task<StreamingEnvOutcome> EnsureDriverSettingsAsync(CancellationToken token);

    /// <summary>
    /// 把已存在显示路径的虚拟屏接进桌面布局。执行体在交互会话（ChangeDisplaySettingsEx 与厂商工具
    /// "Connect This Device" 同族）；实现负责请求并等待客户端的新鲜拓扑回执。
    /// </summary>
    Task<StreamingEnvOutcome> EnsureVirtualDisplayInDesktopAsync(CancellationToken token);

    /// <summary>启用 / 禁用虚拟显示设备。禁用不删驱动：一键卸载要能在下次一键时秒级恢复。</summary>
    Task<StreamingEnvOutcome> SetVirtualDisplayEnabledAsync(bool enabled, CancellationToken token);

    /// <summary>
    /// 写入或还原 Sunshine 配置键（白名单键，写前备份，保留用户其它键）。
    /// Activate 与 Takeover 分开：前者不停用物理屏，是把虚拟屏拉进桌面的安全阶段。
    /// </summary>
    Task<StreamingEnvOutcome> ApplySunshineConfigurationAsync(SunshineConfigMode mode, CancellationToken token);

    /// <summary>重启 Sunshine 服务并等待其重新枚举显示输出。</summary>
    Task<StreamingEnvOutcome> RestartSunshineAsync(CancellationToken token);

    /// <summary>把物理显示器设回活动且为主显（虚拟屏占着主显时，仅点亮屏幕不足以交还控制权）。</summary>
    Task<StreamingEnvOutcome> RestorePhysicalDisplayAsync(CancellationToken token);
}
