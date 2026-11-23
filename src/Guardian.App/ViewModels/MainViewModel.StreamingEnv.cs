using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Guardian.App.Services;
using Guardian.Contracts.Models;
using Guardian.Windows.Display;
using Guardian.Windows.Streaming;

namespace Guardian.App.ViewModels;

/// <summary>主窗口的"串流环境"部分：一键启用/一键卸载的呈现与发起。</summary>
public sealed partial class MainViewModel
{
    private readonly StreamingEnvWatcher _streamingEnv = null!;
    private StreamingEnvironmentProfile? _streamingEnvProfile;
    private bool _streamingEnvBusy;
    private bool _showStreamingEnvDetails;
    private string _streamingEnvActivity = string.Empty;
    private RelayCommand? _enableStreamingEnvCommand;
    private RelayCommand? _revertStreamingEnvCommand;
    private RelayCommand? _refreshStreamingEnvCommand;
    private RelayCommand? _toggleStreamingEnvDetailsCommand;

    public RelayCommand EnableStreamingEnvCommand =>
        _enableStreamingEnvCommand ??= new RelayCommand(_ => { _ = ApplyStreamingEnvAsync(StreamingEnvOp.Enable); });

    public RelayCommand RevertStreamingEnvCommand =>
        _revertStreamingEnvCommand ??= new RelayCommand(_ => { _ = ApplyStreamingEnvAsync(StreamingEnvOp.Revert); });

    public RelayCommand RefreshStreamingEnvCommand =>
        _refreshStreamingEnvCommand ??= new RelayCommand(_ => { _ = RefreshStreamingEnvAsync(); });

    public RelayCommand ToggleStreamingEnvDetailsCommand =>
        _toggleStreamingEnvDetailsCommand ??= new RelayCommand(_ => ToggleStreamingEnvDetails());

    /// <summary>状态四态文案。Busy 必须单独显示：一键过程可能跑几分钟（实测国内直连下载 33 MB 约 3 分钟）。</summary>
    public string StreamingEnvStateText => _streamingEnvProfile?.State switch
    {
        StreamingEnvState.Busy => L("Str.EnvStateBusy"),
        StreamingEnvState.Ready => L("Str.EnvStateReady"),
        StreamingEnvState.Reverted => L("Str.EnvStateReverted"),
        StreamingEnvState.SunshineMissing => L("Str.EnvStateMissing"),
        StreamingEnvState.Activating => L("Str.EnvStateActivating"),
        StreamingEnvState.PartialFailure => L("Str.EnvStatePartial"),
        _ => L("Str.EnvStateUnknown"),
    };

    public Brush StreamingEnvStateBrush => _streamingEnvProfile?.State switch
    {
        StreamingEnvState.Ready => GetBrush("SuccessBrush"),
        StreamingEnvState.Busy => GetBrush("AccentBrush"),
        StreamingEnvState.Reverted => GetBrush("SecondaryTextBrush"),
        StreamingEnvState.SunshineMissing => GetBrush("SecondaryTextBrush"),
        StreamingEnvState.Activating => GetBrush("AccentBrush"),
        _ => GetBrush("WarningBrush"),
    };

    /// <summary>一行取证摘要：界面上每句话都要能回溯到探测到的事实。</summary>
    public string StreamingEnvSummaryText
    {
        get
        {
            var profile = _streamingEnvProfile;
            if (profile is null)
            {
                return L("Str.EnvNotProbed");
            }

            var parts = new List<string>
            {
                profile.Sunshine.Installed
                    ? $"{L("Str.EnvSunshine")} {profile.Sunshine.Version}"
                    : L("Str.EnvSunshineMissing"),
                !profile.VirtualDisplay.DevicePresent
                    ? L("Str.EnvDeviceAbsent")
                    : $"{L("Str.EnvDevice")} {(profile.VirtualDisplay.DeviceEnabled ? L("Str.EnvDeviceEnabled") : L("Str.EnvDeviceDisabled"))}",
                profile.Sunshine.Configured ? L("Str.EnvConfigOn") : L("Str.EnvConfigOff"),
            };

            if (profile.Sunshine.ConfigDrifted)
            {
                parts.Add(L("Str.EnvConfigDrift"));
            }

            parts.Add(profile.VirtualDisplay.PhysicalDisplayActive switch
            {
                true => L("Str.EnvPhysicalActive"),
                false => L("Str.EnvPhysicalInactive"),
                _ => L("Str.EnvPhysicalUnknown"),
            });

            return string.Join(" · ", parts);
        }
    }

    public bool StreamingEnvBusy => _streamingEnvBusy;

    public bool StreamingEnvCanEnable => !_streamingEnvBusy && _streamingEnvProfile?.CanEnable == true;

    public bool StreamingEnvCanRevert => !_streamingEnvBusy && _streamingEnvProfile?.CanRevert == true;

    public bool ShowStreamingEnvDetails => _showStreamingEnvDetails;

    public bool ShowStreamingEnvBlocked => !string.IsNullOrWhiteSpace(_streamingEnvProfile?.BlockedReason);

    public string StreamingEnvBlockedText => _streamingEnvProfile?.BlockedReason ?? string.Empty;

    /// <summary>过程文本：优先用服务端档案里的进度（下载进度只有它有），其次才是本地诊断信息。</summary>
    public string StreamingEnvActivityText
        => string.IsNullOrWhiteSpace(_streamingEnvProfile?.ActivityText)
            ? _streamingEnvActivity
            : _streamingEnvProfile!.ActivityText;

    public ObservableCollection<StreamingEnvStepViewModel> StreamingEnvSteps { get; } = new();

    /// <summary>
    /// 钉死产物清单（官方地址 + 期望 SHA256）。放在界面上而不是只写在文档里，
    /// 用户才能自行按地址取安装包、核对哈希后放进离线目录——我们替他校验，但也让他能验我们。
    /// </summary>
    public ObservableCollection<StreamingEnvStepViewModel> StreamingEnvAssetLines { get; } = new();

    private void EnsureStreamingEnvAssetsListed()
    {
        if (StreamingEnvAssetLines.Count > 0)
        {
            return;
        }

        foreach (var asset in new[] { StreamingEnvAssets.SunshineInstaller, StreamingEnvAssets.VirtualDisplayDriver })
        {
            StreamingEnvAssetLines.Add(new StreamingEnvStepViewModel(
                asset.Name,
                asset.Version,
                GetBrush("SecondaryTextBrush"),
                $"{asset.FileName} · {asset.SizeBytes:N0} B · SHA256 {asset.Sha256}\n{asset.Url}",
                DateTimeOffset.Now));
        }
    }

    private async Task ApplyStreamingEnvAsync(StreamingEnvOp op)
    {
        if (_streamingEnvBusy)
        {
            return;
        }

        _streamingEnvBusy = true;
        RaiseStreamingEnvChanged();

        try
        {
            var started = await _client.ApplyStreamingEnvAsync(op, "Guardian.App");
            if (started is not null)
            {
                StreamingEnvUiThread(() => ApplyStreamingEnvProfile(started));
            }

            await PollStreamingEnvUntilIdleAsync();
        }
        catch (GuardianClientException ex)
        {
            _streamingEnvActivity = $"{L("Str.EnvApplyFailed")}：{ex.Message}";
        }
        finally
        {
            _streamingEnvBusy = false;
            RaiseStreamingEnvChanged();
        }
    }

    private async Task RefreshStreamingEnvAsync()
    {
        try
        {
            var profile = await _streamingEnv.PollOnceAsync();
            if (profile is not null)
            {
                StreamingEnvUiThread(() => ApplyStreamingEnvProfile(profile));
            }
        }
        catch (GuardianClientException ex)
        {
            _streamingEnvActivity = $"{L("Str.EnvProbeFailed")}：{ex.Message}";
            OnPropertyChanged(nameof(StreamingEnvActivityText));
        }
    }

    /// <summary>
    /// 执行期间按秒轮询取分步进度：管道是请求/应答模型、服务端推不了，
    /// 不轮询就只能等整件事跑完才看到结果，那期间界面和卡死没区别。
    /// 30 分钟封顶：超时后保留最后一次档案如实显示，不猜它"应该已经完成"。
    /// </summary>
    private async Task PollStreamingEnvUntilIdleAsync()
    {
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromMinutes(30).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(1500);

            StreamingEnvironmentProfile? profile;
            try
            {
                profile = await _streamingEnv.PollOnceAsync();
            }
            catch (GuardianClientException)
            {
                continue;
            }

            if (profile is null)
            {
                continue;
            }

            StreamingEnvUiThread(() => ApplyStreamingEnvProfile(profile));
            if (profile.State != StreamingEnvState.Busy)
            {
                return;
            }
        }
    }

    private void ApplyStreamingEnvProfile(StreamingEnvironmentProfile profile)
    {
        _streamingEnvProfile = profile;
        StreamingEnvSteps.Clear();
        foreach (var step in profile.Steps)
        {
            StreamingEnvSteps.Add(new StreamingEnvStepViewModel(
                L($"Str.EnvStep.{step.Step}"),
                L($"Str.EnvOutcome.{step.Outcome}"),
                GetBrush(step.Outcome switch
                {
                    StreamingEnvStepOutcome.Ok => "SuccessBrush",
                    StreamingEnvStepOutcome.Skipped => "SecondaryTextBrush",
                    StreamingEnvStepOutcome.Blocked => "WarningBrush",
                    _ => "DangerBrush",
                }),
                step.Evidence,
                step.At.ToLocalTime()));
        }

        RaiseStreamingEnvChanged();
    }

    private void ToggleStreamingEnvDetails()
    {
        _showStreamingEnvDetails = !_showStreamingEnvDetails;
        if (_showStreamingEnvDetails)
        {
            EnsureStreamingEnvAssetsListed();
        }
        OnPropertyChanged(nameof(ShowStreamingEnvDetails));
    }

    private void RaiseStreamingEnvChanged()
    {
        OnPropertyChanged(nameof(StreamingEnvStateText));
        OnPropertyChanged(nameof(StreamingEnvStateBrush));
        OnPropertyChanged(nameof(StreamingEnvSummaryText));
        OnPropertyChanged(nameof(StreamingEnvCanEnable));
        OnPropertyChanged(nameof(StreamingEnvCanRevert));
        OnPropertyChanged(nameof(StreamingEnvBusy));
        OnPropertyChanged(nameof(ShowStreamingEnvBlocked));
        OnPropertyChanged(nameof(StreamingEnvBlockedText));
    }

    /// <summary>
    /// 服务端请求"把虚拟屏接进桌面"时的本地执行体：ChangeDisplaySettingsEx 与厂商工具
    /// Connect This Device 同族，只能由交互会话执行（Session 0 动不了桌面拓扑）。
    /// </summary>
    private Task<string?> AttachVirtualDisplayAsync() => Task.Run<string?>(() =>
    {
        App.DiagLog("[streaming-env] attach virtual display requested by service");
        var ok = DisplayReattacher.AttachVirtualDisplayToDesktop();
        var after = SessionDisplayFactsReader.Read();
        App.DiagLog(ok
            ? $"[streaming-env] virtual display attached, monitors={after.ActiveMonitorCount}"
            : $"[streaming-env] virtual display attach failed, monitors={after.ActiveMonitorCount}");
        return ok ? L("Str.EnvAttachDone") : L("Str.EnvAttachFailed");
    });

    /// <summary>
    /// 服务端请求"把物理屏夺回主显"时的本地执行体：Session 0 改不了显示拓扑，只能在这里做。
    /// 只剩虚拟屏一块输出时不做重挂载（那正是需要 Sunshine 还原拓扑的状态），串流进行中同样跳过——会与 Sunshine 的拓扑切换打架。
    /// </summary>
    private Task<string?> RestorePhysicalDisplayAsync() => Task.Run<string?>(() =>
    {
        if (_streaming.IsStreamingActive())
        {
            return L("Str.EnvRestoreSkippedWhileStreaming");
        }

        var facts = SessionDisplayFactsReader.Read();
        if (facts.ActiveMonitorCount == 0)
        {
            return L("Str.EnvRestoreSkippedNoDisplay");
        }

        var ok = DisplayReattacher.ReattachPhysicalDisplays(_streaming.IsStreamingActive);
        return ok ? L("Str.EnvRestoreDone") : L("Str.EnvRestoreFailed");
    });

    /// <summary>
    /// 紧急键的"夺回物理主显"。点亮屏幕只解决背光：物理显示器被 Sunshine 停用时（ensure_only_display
    /// 接管形态）面板根本拿不回来——本机实测物理面板只在"摘下→插回"事件时才点亮。
    /// 只在确实看不到活动物理屏时才重挂载：正常状态下无谓重挂载会让用户白闪一次。
    /// 与一键恢复的顺序讲究相反，这里串流进行中也要执行——按紧急键就是用户明确要回机器。
    /// </summary>
    private void TryReclaimPhysicalPrimary()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var facts = SessionDisplayFactsReader.Read();
                if (facts.ActiveMonitorCount == 0 || facts.PhysicalDisplayActive)
                {
                    return;
                }

                // 分支必须写清楚：桌面里有物理屏 = 走"摘下→插回"点亮；桌面里没有 = 走"接回桌面"。
                // 昨天实测失败就是因为只有后者才需要起作用，而日志看不出走了哪条。
                var branch = facts.PhysicalDisplayActive ? "摘下→插回" : "接回桌面";
                App.DiagLog($"[emergency] 仅剩虚拟屏活动（monitors={facts.ActiveMonitorCount}），重新挂载物理屏（{branch}）");
                var ok = DisplayReattacher.ReattachPhysicalDisplays(() => false);
                var after = SessionDisplayFactsReader.Read();
                App.DiagLog(ok
                    ? $"[emergency] 物理屏已重新挂载（{branch}），现在活动屏幕数={after.ActiveMonitorCount}"
                    : $"[emergency] 物理屏重新挂载失败（{branch}），活动屏幕数={after.ActiveMonitorCount}");

                // 拓扑刚被改动，立刻回执给服务端：别让互锁拿着旧事实判断下一步。
                await _streamingEnv.PollOnceAsync();
            }
            catch (Exception ex)
            {
                App.DiagLog($"[emergency] 夺回物理屏异常：{ex.Message}");
            }
        });
    }

    private static void StreamingEnvUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
