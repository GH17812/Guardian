using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Infrastructure.Ipc;
using Guardian.Storage.Repositories;
using Guardian.Windows.Input;
using Microsoft.Extensions.Hosting;

namespace Guardian.Service;

/// <summary>
/// 服务生命周期宿主：
/// 1. 能力检测（输入设备枚举 + 历史显示隐私验证结果）；
/// 2. 载入持久化会话；
/// 3. 启动恢复（fail-open，无条件释放保护资源）；
/// 4. 按 ResumePolicy 决定是否恢复上次保护；
/// 5. 启动 IPC 管道；
/// 停止时：关闭管道 → 引擎 Shutdown（触发 ServiceStop 恢复流程）。
/// </summary>
public sealed class GuardianHost : BackgroundService
{
    private readonly ProtectionEngine _engine;
    private readonly GuardianPipeServer _pipeServer;
    private readonly SessionRepository _sessionRepository;
    private readonly CapabilityRepository _capabilityRepository;
    private readonly IGuardianLog _log;

    public GuardianHost(
        ProtectionEngine engine,
        GuardianPipeServer pipeServer,
        SessionRepository sessionRepository,
        CapabilityRepository capabilityRepository,
        IGuardianLog log)
    {
        _engine = engine;
        _pipeServer = pipeServer;
        _sessionRepository = sessionRepository;
        _capabilityRepository = capabilityRepository;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Write(GuardianLogLevel.Information, "service", $"Guardian Service v{ProductInfo.Version} 正在启动");

        try
        {
            InitializeCapabilities();
            _engine.LoadPersistedSessions(_sessionRepository.GetActive());
            _engine.StartupRestore();

            if (_engine.GetSettings().ResumePolicy == ResumePolicy.ResumeLast)
            {
                _engine.ResumeAfterRestart();
            }

            // 待机模式武装状态持久化：服务重启后恢复"已武装"（睡眠会转成离开模式，串流保持可达），
            // 但不恢复待机会话——否则开机那一刻就持有离开模式，实测会让桌面无声。
            if (_engine.GetSettings().RestModeArmed)
            {
                _engine.EnableRestMode("service-startup", enterSession: false);
            }

            _pipeServer.Start();
            _log.Write(GuardianLogLevel.Information, "service", "服务已就绪，IPC 管道已启动",
                $"pipe={ProtocolConstants.PipeName}");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Critical, "service", "服务运行期异常", ex.ToString());
        }
        finally
        {
            try
            {
                await _pipeServer.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.Write(GuardianLogLevel.Error, "service", "关闭管道异常", ex.Message);
            }
            _engine.Shutdown();
            _log.Write(GuardianLogLevel.Information, "service", "服务已停止（保护资源已释放）");
        }
    }

    private void InitializeCapabilities()
    {
        var devices = InputCapabilityDetector.Detect();
        var stored = _capabilityRepository.GetLatest();

        var report = new CapabilityReport
        {
            GeneratedAt = DateTimeOffset.Now,
            InputDevices = devices,
            RemoteInputDetection = RemoteInputDetectionCapability.Supported,
            RemoteInputDetectionNote =
                "基于低层钩子 INJECTED 标记区分远程注入输入。注意：部分远程方案（如 RDP 输入栈）的输入可能不带该标记，存在误拦截风险，启用前须在本机验证。",
            InputIsolationExperimentalAvailable = true,
            EnhancedIsolationAvailable = false,
            Notes =
            {
                "输入隔离为 Phase 1 实验性能力，默认关闭，启用需双重确认。",
                "输入隔离由客户端（交互会话）低层钩子执行；服务端 Session 0 钩子无法监听交互会话输入，仅作状态权威。",
                "增强隔离（触摸屏/触控板/手写笔/手柄）为 Phase 2 能力，本版本未实现。",
                "紧急恢复快捷键：Ctrl+Alt+Shift+主键（默认 F12），由客户端全局热键与输入隔离钩子识别，不依赖 UI 可用性。",
            },
        };

        if (devices.Count == 0)
        {
            // Session 0 服务无法枚举交互会话输入设备，等待客户端（用户会话）上报。
            report.Notes.Add("服务运行于 Session 0，无法枚举交互会话输入设备；客户端连接后将自动上报设备清单。");
        }

        if (stored?.DisplayPrivacyValidated is { } validated)
        {
            report.DisplayPrivacyValidated = validated;
            report.DisplayPrivacyValidatedAt = stored.DisplayPrivacyValidatedAt;
        }

        _engine.InitializeCapabilities(report);
        _log.Write(GuardianLogLevel.Information, "service", "能力检测完成",
            $"devices=[{string.Join(", ", devices.Select(d => $"{d.DeviceType}:{d.Count}"))}]");
    }
}
