using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Ports;

namespace Guardian.Core.Engine;

/// <summary>
/// 串流环境协调器：把"一键启用 / 一键卸载"拆成可取证、可回退的分步动作，
/// 并在写入会停用物理屏的配置之前执行硬互锁。
/// </summary>
public sealed class StreamingEnvCoordinator
{
    /// <summary>互锁命中时给用户的解释（界面原样展示，不得改写成"成功"）。</summary>
    public const string InterlockNoDisplayPath =
        "虚拟显示适配器没有可用的显示路径（EnumDisplayDevices 级别都没看到），已停止：这种情况下写任何 dd_config 都不会产出屏幕，继续只会让本机少一块能用的屏幕。";

    /// <summary>ensure_primary 之后虚拟屏仍不在桌面布局里：停在安全中间态，不停物理屏。</summary>
    public const string InterlockNotActivated =
        "虚拟屏还没进入桌面布局，已停在 ensure_primary 阶段：物理屏仍在工作，本机画面没有丢。可重试一键启用，或点一键恢复回到原形态。";

    /// <summary>默认等待窗口：Sunshine 只在启动时把 dd_config 落到拓扑上，实测一两秒内可见。</summary>
    private const int DefaultDesktopWaitAttempts = 10;

    /// <summary>
    /// 设备刚启用后要等显示路径挂上来（约 15 秒）。实测驱动造屏可能更久：21:42:35 等 15 秒判"没有路径"，
    /// 同一块 DISPLAY8 到 21:49 才带监视器对象出现。窗口不再放大——用户实测当前时长可用，
    /// 而拦停时界面会如实显示原因，再点一次即可继续。
    /// </summary>
    private const int DefaultPathWaitAttempts = 25;

    private static readonly TimeSpan DefaultDesktopWaitInterval = TimeSpan.FromMilliseconds(600);

    private readonly IStreamingEnvironmentPort _port;
    private readonly IClock _clock;
    private readonly IGuardianLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _desktopWaitAttempts;
    private readonly int _pathWaitAttempts;
    private readonly TimeSpan _desktopWaitInterval;

    private StreamingEnvironmentProfile _current = new() { State = StreamingEnvState.Unknown };
    private string? _lastLoggedSignature;

    public StreamingEnvCoordinator(
        IStreamingEnvironmentPort port,
        IClock clock,
        IGuardianLog log,
        int desktopWaitAttempts = DefaultDesktopWaitAttempts,
        TimeSpan? desktopWaitInterval = null,
        int pathWaitAttempts = DefaultPathWaitAttempts)
    {
        _port = port;
        _clock = clock;
        _log = log;
        _desktopWaitAttempts = desktopWaitAttempts;
        _pathWaitAttempts = pathWaitAttempts;
        _desktopWaitInterval = desktopWaitInterval ?? DefaultDesktopWaitInterval;
    }

    /// <summary>分步进度出口。实现须快速返回；服务端据此转发到界面与日志。</summary>
    public event Action<StreamingEnvironmentProfile>? Progress;

    public StreamingEnvironmentProfile Current => _current;

    /// <summary>只读刷新探测结果，不改动系统。</summary>
    public StreamingEnvironmentProfile Refresh() => Publish(_port.Probe(), _current.Steps, null, _current.BlockedReason);

    public Task<StreamingEnvironmentProfile> EnableAsync(CancellationToken token = default) =>
        RunAsync(StreamingEnvOp.Enable, token);

    public Task<StreamingEnvironmentProfile> RevertAsync(CancellationToken token = default) =>
        RunAsync(StreamingEnvOp.Revert, token);

    private async Task<StreamingEnvironmentProfile> RunAsync(StreamingEnvOp op, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
        {
            return _current;
        }

        try
        {
            return op == StreamingEnvOp.Enable
                ? await RunEnableAsync(token).ConfigureAwait(false)
                : await RunRevertAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StreamingEnvironmentProfile> RunEnableAsync(CancellationToken token)
    {
        var steps = NewSteps(StreamingEnvStep.SunshineInstalled, StreamingEnvStep.VirtualDisplayDriver,
            StreamingEnvStep.VirtualDisplayEnabled, StreamingEnvStep.VirtualDisplayEnumerated,
            StreamingEnvStep.VirtualDisplayAttached,
            StreamingEnvStep.SunshineActivating, StreamingEnvStep.SunshineTakeover, StreamingEnvStep.SunshineRunning);

        var probed = _port.Probe();
        Publish(probed, steps, StreamingEnvOp.Enable, null);

        var sunshineOk = probed.Sunshine.Installed
            ? Record(steps, StreamingEnvStep.SunshineInstalled, StreamingEnvStepOutcome.Skipped,
                $"已安装：{probed.Sunshine.Version}")
            : await RunStepAsync(steps, StreamingEnvStep.SunshineInstalled,
                () => _port.EnsureSunshineInstalledAsync(token),
                "Sunshine 安装失败", "未检测到 Sunshine，自动安装也未成功。").ConfigureAwait(false);

        // Sunshine 缺失时后续步骤全无意义：拓扑切换由 Sunshine 执行，写了配置也没人能还原回来。
        if (!sunshineOk)
        {
            return Finish(steps, BlockRemaining(steps, "Sunshine 未安装成功，已停止：没有组件会把显示拓扑切回来。"));
        }

        var driverOk = probed.VirtualDisplay.DevicePresent
            ? Record(steps, StreamingEnvStep.VirtualDisplayDriver, StreamingEnvStepOutcome.Skipped,
                $"根设备已存在：{probed.VirtualDisplay.InstanceId}")
            : await RunStepAsync(steps, StreamingEnvStep.VirtualDisplayDriver,
                () => _port.EnsureVirtualDisplayInstalledAsync(token),
                "虚拟显示驱动安装失败", "本机没有虚拟显示根设备，驱动安装未成功。").ConfigureAwait(false);

        var enabledOk = driverOk && (probed.VirtualDisplay.DeviceEnabled
            ? Record(steps, StreamingEnvStep.VirtualDisplayEnabled, StreamingEnvStepOutcome.Skipped,
                "根设备已处于启用状态")
            : await RunStepAsync(steps, StreamingEnvStep.VirtualDisplayEnabled,
                () => _port.SetVirtualDisplayEnabledAsync(true, token),
                "虚拟显示设备已启用", "虚拟显示设备启用失败。").ConfigureAwait(false));

        if (!enabledOk)
        {
            return Finish(steps, BlockRemaining(steps, "虚拟显示设备未能启用，已停止：不写 Sunshine 配置，物理屏不受影响。"));
        }

        // 互锁一（安全前提）：连显示路径都没有时，写任何 dd_config 都不会产出屏幕；
        // 继续下去只会让本机少一块能用的屏幕。这条必须在写配置之前，且没有任何例外。
        // 但设备启用是异步的，必须等到路径出现或窗口耗尽，不能读一次就下结论。
        // 等路径之前先确保驱动的建屏设置落位：没有它，驱动启动时一个监视器都不创建，
        // 后面无论怎么等设备、重启设备都是白费（新机 15:03 那次就是这个状态）。
        var settingsNote = string.Empty;
        try
        {
            var seeded = await _port.EnsureDriverSettingsAsync(token).ConfigureAwait(false);
            settingsNote = seeded.Ok ? seeded.Evidence : "设置未落位：" + seeded.Evidence;
        }
        catch (Exception ex)
        {
            settingsNote = "设置检查异常：" + ex.Message;
        }

        _log.Write(GuardianLogLevel.Information, "streaming-env", "等待虚拟显示路径出现", settingsNote);
        var withPath = await WaitForAsync(p => p.VirtualDisplay.PathActive, _pathWaitAttempts).ConfigureAwait(false);

        if (withPath is null)
        {
            // 等过之后还没有路径，先重启一次虚拟设备再判死：驱动只在设备启动时按自己的设置创建监视器对象，
            // 而新机的根设备是刚建出来的，很可能以"零监视器"状态醒来（新机实测正是停在这里）。
            // 这个动作只碰虚拟适配器、不停物理屏，放在互锁之前是安全的；
            // 把它藏在"取到 output_name 之后"就形成死锁 —— 没路径就取不到标识，也就永远走不到自举。
            _log.Write(GuardianLogLevel.Information, "streaming-env", "仍无显示路径，重启虚拟设备以促使驱动创建监视器");
            await SetVirtualDisplayEnabledSafeAsync(false, token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            await SetVirtualDisplayEnabledSafeAsync(true, token).ConfigureAwait(false);
            withPath = await WaitForAsync(p => p.VirtualDisplay.PathActive, _pathWaitAttempts).ConfigureAwait(false);
        }

        if (withPath is null)
        {
            var waited = (int)(_desktopWaitInterval.TotalMilliseconds * _pathWaitAttempts / 1000) * 2;
            var reason = $"{InterlockNoDisplayPath}"
                + $"（已等待并重启过一次虚拟设备，累计约 {waited} 秒仍无监视器对象）。"
                + $"建屏设置：{settingsNote}。"
                + "若设置已落位仍无监视器，用厂商工具 VDD Control 执行一次 SETCOUNT 1 + Restart Driver 再点一键启用；"
                + "若其日志出现 Failed to set device to swap chain，那是上游驱动的限制，不是本机配置问题。";
            Record(steps, StreamingEnvStep.VirtualDisplayEnumerated, StreamingEnvStepOutcome.Blocked, reason);
            return Finish(steps, BlockRemaining(steps, reason));
        }

        Record(steps, StreamingEnvStep.VirtualDisplayEnumerated, StreamingEnvStepOutcome.Ok,
            $"显示路径可用：{withPath.VirtualDisplay.OutputGuid ?? withPath.VirtualDisplay.FriendlyName}");

        // 路径有了，但可能还不在桌面布局里。实测：Sunshine 只在客户端连接时才动拓扑，
        // 而新机上客户端连不上，于是永远卡在"没进桌面"。接屏这步改由 Guardian 自己做：
        // 客户端交互会话执行（ChangeDisplaySettingsEx，与厂商工具 Connect This Device 同族），
        // 服务端等待新鲜回执确认虚拟屏可枚举。
        var beforeAttach = _port.Probe();
        if (!beforeAttach.VirtualDisplay.Enumerated)
        {
            var attached = await RunStepAsync(steps, StreamingEnvStep.VirtualDisplayAttached,
                () => _port.EnsureVirtualDisplayInDesktopAsync(token),
                "虚拟屏已接进桌面布局", "把虚拟屏接进桌面布局未成功。").ConfigureAwait(false);

            if (!attached)
            {
                var reason = "虚拟屏没能进入桌面布局，已停止："
                    + (LastFailureOf(steps) ?? "接屏未确认。");
                return Finish(steps, BlockRemaining(steps, reason));
            }
        }
        else
        {
            Record(steps, StreamingEnvStep.VirtualDisplayAttached, StreamingEnvStepOutcome.Skipped,
                "虚拟屏已在桌面布局里");
        }

        // 接管分两阶段。原因由实测得出：物理屏形态下虚拟路径常在但不进桌面布局，
        // 而把它拉进桌面的正是 dd_config，直接写「只留虚拟屏」会被互锁永远拦住（结构性启不来），
        // 不互锁又可能让本机没有输出。ensure_primary 只激活、不停物理屏，是唯一安全的中间态。
        var activated = await RunStepAsync(steps, StreamingEnvStep.SunshineActivating,
            async () =>
            {
                var written = await _port.ApplySunshineConfigurationAsync(SunshineConfigMode.Activate, token).ConfigureAwait(false);
                if (!written.Ok)
                {
                    return written;
                }

                var restarted = await _port.RestartSunshineAsync(token).ConfigureAwait(false);
                return restarted.Ok
                    ? new StreamingEnvOutcome(true, $"{written.Evidence}；{restarted.Evidence}")
                    : new StreamingEnvOutcome(false, $"{written.Evidence}；但服务重启失败：{restarted.Evidence}");
            },
            "已写入 ensure_primary 并重启 Sunshine", "第一阶段（激活虚拟屏）未成功。").ConfigureAwait(false);

        if (!activated)
        {
            return Finish(steps, BlockRemaining(steps, LastFailureOf(steps) ?? "第一阶段未成功，未停用物理屏。"));
        }

        // Sunshine 在启动时才把 dd_config 落到显示拓扑上，激活效果要等一会儿才可见。
        var inDesktop = await WaitForAsync(p => p.VirtualDisplay.Enumerated, _desktopWaitAttempts).ConfigureAwait(false);
        if (inDesktop is null)
        {
            // 停在 ensure_primary 阶段：物理屏仍在工作，本机画面没丢，因此是"拦停"而不是"失败"。
            var reason = $"{InterlockNotActivated}（当前虚拟屏状态：路径可用、尚未进入桌面布局）";
            Record(steps, StreamingEnvStep.SunshineTakeover, StreamingEnvStepOutcome.Blocked, "未执行。");
            Record(steps, StreamingEnvStep.SunshineRunning, StreamingEnvStepOutcome.Blocked, "未执行。");
            return Finish(steps, reason);
        }

        Record(steps, StreamingEnvStep.SunshineTakeover, StreamingEnvStepOutcome.Ok,
            $"虚拟屏已进入桌面布局（{inDesktop.VirtualDisplay.OutputGuid ?? inDesktop.VirtualDisplay.InstanceId}），允许停用物理屏");

        var takeover = await RunStepAsync(steps, StreamingEnvStep.SunshineTakeover,
            () => _port.ApplySunshineConfigurationAsync(SunshineConfigMode.Takeover, token),
            "Sunshine 配置已写入接管形态", "配置写入失败：文件可能被占用或权限不足。").ConfigureAwait(false);

        var restarted = takeover && await RunStepAsync(steps, StreamingEnvStep.SunshineRunning,
            () => _port.RestartSunshineAsync(token),
            "Sunshine 服务已重启", "服务重启失败：配置尚未生效，仍是原形态。").ConfigureAwait(false);

        return Finish(steps, restarted ? null : LastFailureOf(steps));
    }

    /// <summary>互锁前的设备重启：失败只记录，不改变"到底有没有路径"这个最终判据。</summary>
    private async Task SetVirtualDisplayEnabledSafeAsync(bool enabled, CancellationToken token)
    {
        try
        {
            await _port.SetVirtualDisplayEnabledAsync(enabled, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Warning, "streaming-env",
                $"虚拟设备重启（{(enabled ? "启用" : "禁用")}）未成功", ex.Message);
        }
    }

    /// <summary>轮询到条件满足为止；超时返回 null（调用方按"未确认"处理，绝不猜成功）。</summary>
    private async Task<StreamingEnvironmentProfile?> WaitForAsync(Func<StreamingEnvironmentProfile, bool> predicate, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var profile = _port.Probe();
            if (predicate(profile))
            {
                return profile;
            }

            await Task.Delay(_desktopWaitInterval).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<StreamingEnvironmentProfile> RunRevertAsync(CancellationToken token)
    {
        var steps = NewSteps(StreamingEnvStep.SunshineConfigured, StreamingEnvStep.SunshineRunning,
            StreamingEnvStep.PhysicalDisplayRestored, StreamingEnvStep.VirtualDisplayEnabled);

        var probed = _port.Probe();
        Publish(probed, steps, StreamingEnvOp.Revert, null);

        if (!probed.Sunshine.Installed)
        {
            return Finish(steps, BlockRemaining(steps, "本机未安装 Sunshine，无需还原。"));
        }

        // 顺序讲究：先还原配置并让 Sunshine 用回旧形态，再动物理屏，最后才禁用虚拟设备。
        // 反过来会在 Sunshine 仍要求「只留虚拟屏」时关掉它依赖的那个输出。
        var configured = await RunStepAsync(steps, StreamingEnvStep.SunshineConfigured,
            () => _port.ApplySunshineConfigurationAsync(SunshineConfigMode.Revert, token),
            "Sunshine 配置已还原", "配置还原失败：拓扑接管可能仍然生效。").ConfigureAwait(false);

        var restarted = configured && await RunStepAsync(steps, StreamingEnvStep.SunshineRunning,
            () => _port.RestartSunshineAsync(token),
            "Sunshine 服务已重启", "服务重启失败：还原后的配置尚未生效。").ConfigureAwait(false);

        // 这一步即使前面失败也要试：用户至少要把物理屏拿回来，它不属于"环境是否漂亮"而属于"能否继续使用机器"。
        await RunStepAsync(steps, StreamingEnvStep.PhysicalDisplayRestored,
            () => _port.RestorePhysicalDisplayAsync(token),
            "物理显示器已回到主显", "物理显示器未能确认回到主显：用紧急恢复键 Ctrl+Alt+Shift+F12 交还控制权。")
            .ConfigureAwait(false);

        await RunStepAsync(steps, StreamingEnvStep.VirtualDisplayEnabled,
            () => _port.SetVirtualDisplayEnabledAsync(false, token),
            "虚拟设备已禁用（驱动保留，下次一键启用无需重装）",
            "虚拟设备禁用失败：可能仍多出一块屏，但 Sunshine 已不再接管拓扑。").ConfigureAwait(false);

        return Finish(steps, restarted ? null : LastFailureOf(steps));
    }

    private async Task<bool> RunStepAsync(List<StreamingEnvStepResult> steps, StreamingEnvStep step,
        Func<Task<StreamingEnvOutcome>> action, string okPrefix, string failurePrefix)
    {
        StreamingEnvOutcome outcome;
        try
        {
            outcome = await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Record(steps, step, StreamingEnvStepOutcome.Blocked, "已取消。");
            return false;
        }
        catch (Exception ex)
        {
            Record(steps, step, StreamingEnvStepOutcome.Failed, $"{failurePrefix}：{ex.Message}");
            return false;
        }

        // 端口给出的实测值（版本、路径、设备实例）拼在后缀上，界面上要能看出"这一步看到了什么"。
        Record(steps, step,
            outcome.Ok ? StreamingEnvStepOutcome.Ok : StreamingEnvStepOutcome.Failed,
            outcome.Ok ? Combine(okPrefix, outcome.Evidence) : Combine(failurePrefix, outcome.Evidence));

        return outcome.Ok;
    }

    private bool Record(List<StreamingEnvStepResult> steps, StreamingEnvStep step,
        StreamingEnvStepOutcome outcome, string evidence)
    {
        var index = steps.FindIndex(s => s.Step == step);
        var result = new StreamingEnvStepResult
        {
            Step = step,
            Outcome = outcome,
            Evidence = evidence,
            At = _clock.UtcNow,
        };

        if (index >= 0)
        {
            steps[index] = result;
        }
        else
        {
            steps.Add(result);
        }

        if (outcome == StreamingEnvStepOutcome.Failed)
        {
            _log.Write(GuardianLogLevel.Warning, "streaming-env", $"{step} 失败", evidence);
        }

        return outcome == StreamingEnvStepOutcome.Ok || outcome == StreamingEnvStepOutcome.Skipped;
    }

    private string BlockRemaining(List<StreamingEnvStepResult> steps, string reason)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Outcome != StreamingEnvStepOutcome.Pending)
            {
                continue;
            }

            steps[i] = new StreamingEnvStepResult
            {
                Step = steps[i].Step,
                Outcome = StreamingEnvStepOutcome.Blocked,
                Evidence = "未执行。",
                At = _clock.UtcNow,
            };
        }

        return reason;
    }

    private StreamingEnvironmentProfile Finish(List<StreamingEnvStepResult> steps, string? blockedReason) =>
        Publish(_port.Probe(), steps, null, blockedReason);

    private static string Combine(string prefix, string? evidence) =>
        string.IsNullOrWhiteSpace(evidence) ? prefix : $"{prefix}（{evidence}）";

    private static List<StreamingEnvStepResult> NewSteps(params StreamingEnvStep[] steps) =>
        steps.Select(s => new StreamingEnvStepResult { Step = s, Outcome = StreamingEnvStepOutcome.Pending }).ToList();

    private static string? LastFailureOf(List<StreamingEnvStepResult> steps)
    {
        var failed = steps.LastOrDefault(s => s.Outcome == StreamingEnvStepOutcome.Failed);
        if (failed != null)
        {
            return failed.Evidence;
        }

        return steps.LastOrDefault(s => s.Outcome == StreamingEnvStepOutcome.Blocked &&
            s.Step != StreamingEnvStep.VirtualDisplayEnumerated)?.Evidence;
    }

    private StreamingEnvironmentProfile Publish(StreamingEnvironmentProfile probed,
        List<StreamingEnvStepResult> steps, StreamingEnvOp? runningOp, string? blockedReason)
    {
        var state = runningOp != null ? StreamingEnvState.Busy : DeriveState(probed);

        // 配置已落盘但某一步失败（典型：服务没重启成，当前进程仍持旧配置）= 部分生效，
        // 不能因为事实条件凑齐了就报"已就绪"：那会让界面把一个还差一口气的环境说成完成。
        if (state == StreamingEnvState.Ready && steps.Any(s => s.Outcome == StreamingEnvStepOutcome.Failed))
        {
            state = StreamingEnvState.PartialFailure;
        }
        var published = new StreamingEnvironmentProfile
        {
            State = state,
            GeneratedAt = _clock.UtcNow,
            StampMs = Environment.TickCount64,
            Sunshine = probed.Sunshine,
            VirtualDisplay = probed.VirtualDisplay,
            Steps = steps.Select(s => new StreamingEnvStepResult
            {
                Step = s.Step,
                Outcome = s.Outcome,
                Evidence = s.Evidence,
                At = s.At,
            }).ToList(),
            RunningOp = runningOp,
            BlockedReason = blockedReason,
        };

        published.CanEnable = state != StreamingEnvState.Busy && state != StreamingEnvState.Ready;
        published.CanRevert = state != StreamingEnvState.Busy && published.Sunshine.Installed;

        _current = published;
        // 只在"真的变了"时写日志：界面每 3 秒取一次档案，全量刷会让日志失去意义。
        // 但判定必须包含分步签名——一键动作跑完而状态没变（典型：启用被拦停、仍是 Reverted）时，
        // 只看状态会把整次操作的取证吞掉（实测 21:13:23 那次就这样丢了）。
        var signature = $"{published.State}|{string.Join(",", published.Steps.Select(s => $"{s.Step}={s.Outcome}"))}";
        if (runningOp == null && signature != _lastLoggedSignature)
        {
            _lastLoggedSignature = signature;
            _log.Write(GuardianLogLevel.Information, "streaming-env", $"环境状态：{published.State}",
                blockedReason ?? string.Join(" | ", published.Steps.Select(s => $"{s.Step}={s.Outcome}")));
        }

        Progress?.Invoke(published);
        return published;
    }

    /// <summary>
    /// 状态只由事实推导，不看"上次按钮点了什么"：配置没写就是 Sunshine 不接管拓扑，
    /// 即使虚拟设备还醒着也属于用户要的"物理屏亮 + 串流物理屏"形态，因此判为 Reverted 而非失败。
    /// PartialFailure 专门留给"文件与进程不一致"与"配置已写但输出没到位"这类真危险的中间态。
    /// </summary>
    private static StreamingEnvState DeriveState(StreamingEnvironmentProfile p)
    {
        if (!p.Sunshine.Installed)
        {
            return StreamingEnvState.SunshineMissing;
        }

        // 磁盘配置与运行中进程不一致 = 还差一次重启，界面不能替它选一个方向冒充完成态。
        if (p.Sunshine.Configured != p.Sunshine.EffectiveConfigured)
        {
            return StreamingEnvState.PartialFailure;
        }

        if (!p.Sunshine.Configured)
        {
            return StreamingEnvState.Reverted;
        }

        return p.VirtualDisplay.DeviceEnabled && p.VirtualDisplay.Enumerated
            ? StreamingEnvState.Ready
            // 只激活形态是"停在安全半程"，不是失败也不是就绪：物理屏还在工作，虚拟屏归 Sunshine 管了。
            : p.Sunshine.Activating ? StreamingEnvState.Activating : StreamingEnvState.PartialFailure;
    }
}
