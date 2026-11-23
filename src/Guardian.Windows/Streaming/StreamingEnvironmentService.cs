using Guardian.Contracts.Models;
using System.Text;
using Guardian.Core.Ports;

namespace Guardian.Windows.Streaming;

/// <summary>
/// 串流环境端口的真机实现：把探测、下载、静默安装、设备启停、配置读写拼成协调器要的那七个动作。
/// 两条硬规矩贯穿全类：
/// 1. 每个动作都以"做完之后重新探测到的事实"论成败，命令行/API 返回码只作为附注；
/// 2. 拿不到虚拟屏的 output_name 就绝不写 ensure_only_display —— 写错值会让物理屏被停用而系统没有可用输出。
/// </summary>
public sealed class StreamingEnvironmentService : IStreamingEnvironmentPort
{
    private static readonly TimeSpan DefaultTopologyFreshness = TimeSpan.FromSeconds(30);

    /// <summary>重启后等设备清单落地的等待窗口与轮询步进。</summary>
    private static readonly TimeSpan GuidWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan GuidPollInterval = TimeSpan.FromMilliseconds(800);

    private readonly Func<StreamingTopologyFact?> _topology;
    private readonly AssetFetcher _fetcher;
    private readonly TimeSpan _topologyFreshness;

    /// <summary>耗时动作的过程播报（下载进度等），由服务端转成日志与界面进度。</summary>
    public Action<string>? Activity { get; set; }

    /// <summary>
    /// "把物理屏夺回主显"的实际执行者。服务端 Session 0 改不了显示拓扑，必须交给交互会话，
    /// 因此由宿主在组装时注入；没注入时该步骤如实失败，而不是假装已经恢复。
    /// </summary>
    public Func<CancellationToken, Task<StreamingEnvOutcome>>? RestorePhysical { get; set; }

    /// <summary>
    /// 离线兜底目录的取值时机必须在下载当时，而不是构造时：
    /// 用户是先在设置里填目录、再点一键启用的，构造期快照会把新填的目录当成空。
    /// </summary>
    public Func<string?>? OfflineDirectoryProvider { get; set; }

    /// <summary>上次确认过的虚拟屏 output_name（宿主从设置里读；虚拟屏不在桌面时是唯一可用的取值来源）。</summary>
    public Func<string?>? LastKnownOutputGuid { get; set; }

    /// <summary>把当场确认过的 output_name 记回设置，供下一次"物理屏形态"下的一键启用使用。</summary>
    public Action<string>? RememberOutputGuid { get; set; }

    public StreamingEnvironmentService(
        Func<StreamingTopologyFact?> topology,
        AssetFetcher? fetcher = null,
        TimeSpan? topologyFreshness = null)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _fetcher = fetcher ?? new AssetFetcher();
        _topologyFreshness = topologyFreshness ?? DefaultTopologyFreshness;
    }

    private string? OfflineDirectory => OfflineDirectoryProvider?.Invoke();

    public StreamingEnvironmentProfile Probe()
    {
        var sun = SunshineEnvironmentProbe.Read();
        var editor = new SunshineConfigEditor(sun.ConfigFilePath);
        var fileValues = editor.ReadValues();
        var device = VirtualDisplayDevice.Probe();
        var fact = FreshFact();

        var (guid, guidSource) = ResolveOutputGuid(sun, fileValues);
        var configured = !SunshineConfigEditor.IsRevertedForm(fileValues);
        var (effectiveConfigured, effectiveActivating) = ReadEffectiveForm(sun);
        var logSeesVirtual = guidSource.StartsWith("Sunshine 日志", StringComparison.Ordinal);
        var pathActive = logSeesVirtual || fact?.VirtualDisplayPathActive == true;

        return new StreamingEnvironmentProfile
        {
            GeneratedAt = DateTimeOffset.Now,
            StampMs = Environment.TickCount64,
            Sunshine = new SunshineEnvironmentInfo
            {
                Installed = sun.ExecutableExists,
                Version = sun.Version,
                ServiceRunning = sun.ServiceRunning,
                Configured = configured,
                EffectiveConfigured = effectiveConfigured,
                Activating = effectiveActivating,
                ConfigDrifted = configured != effectiveConfigured,
                InstallSource = sun.How,
            },
            VirtualDisplay = new VirtualDisplayEnvironmentInfo
            {
                DevicePresent = device.Present,
                DeviceEnabled = device.Enabled,
                // 互锁要求两侧同时命中：Sunshine 看得见这块屏（否则没人能切拓扑），
                // 交互会话也枚举到了它（否则用户本地没有可用画面）。
                // "进没进桌面"决定能不能停用物理屏：只信交互会话的新鲜上报。
                // 不能用"Sunshine 日志里有它"替代——那是服务端 Session 0 的视角，
                // 而停用物理屏后有没有画面是用户那个会话的事（两个红测正是在拦这条放宽）。
                Enumerated = guid is not null && fact?.VirtualDisplayEnumerated == true,

                // "有没有路径"是更弱的断言（只允许尝试 ensure_primary），日志命中即可算数。
                PathActive = pathActive,
                InstanceId = device.InstanceId,
                FriendlyName = device.Description,
                OutputGuid = guid,
                PhysicalDisplayActive = fact?.PhysicalDisplayActive,
                OutputGuidSource = guidSource,
            },
        };
    }

    public async Task<StreamingEnvOutcome> EnsureSunshineInstalledAsync(CancellationToken token)
    {
        var snapshot = SunshineEnvironmentProbe.Read();
        if (snapshot.ExecutableExists)
        {
            return new StreamingEnvOutcome(true, $"已安装 {snapshot.Version}，位置 {snapshot.InstallDirectory}。");
        }

        if (!StreamingEnvInstaller.SupportsCurrentArchitecture())
        {
            return new StreamingEnvOutcome(false, "本机不是 x64，钉死的 Sunshine 安装包不适用，已停止自动安装。");
        }

        var fetched = await _fetcher
            .FetchAsync(StreamingEnvAssets.SunshineInstaller, OfflineDirectory, DownloadProgress("Sunshine"), token)
            .ConfigureAwait(false);
        if (!fetched.Ok)
        {
            return new StreamingEnvOutcome(false, fetched.Evidence);
        }

        Say($"安装包就绪（{StreamingEnvAssets.SunshineInstaller.Version}），正在静默安装，请勿关机。");
        var installed = await StreamingEnvInstaller.InstallSunshineAsync(fetched.LocalPath, token).ConfigureAwait(false);

        var after = SunshineEnvironmentProbe.Read();
        return installed.Ok && after.ExecutableExists
            ? new StreamingEnvOutcome(true, $"{installed.Evidence} 复查：{after.Version} 位于 {after.InstallDirectory}。")
            : new StreamingEnvOutcome(false, $"{installed.Evidence} 复查：仍未发现 sunshine.exe。");
    }

    public async Task<StreamingEnvOutcome> EnsureVirtualDisplayInstalledAsync(CancellationToken token)
    {
        var existing = VirtualDisplayDevice.Probe();
        if (existing.Present)
        {
            return new StreamingEnvOutcome(true, existing.Evidence);
        }

        if (!StreamingEnvInstaller.SupportsCurrentArchitecture())
        {
            return new StreamingEnvOutcome(false, "钉死的 VDD 驱动包只含 NTamd64 段，非 x64 机器需要另取 ARM64 包并另行取证哈希。");
        }

        token.ThrowIfCancellationRequested();
        var fetched = await _fetcher
            .FetchAsync(StreamingEnvAssets.VirtualDisplayDriver, OfflineDirectory, DownloadProgress("虚拟显示驱动"), token)
            .ConfigureAwait(false);
        if (!fetched.Ok)
        {
            return new StreamingEnvOutcome(false, fetched.Evidence);
        }

        var extracted = StreamingEnvInstaller.ExtractDriver(fetched.LocalPath, out var infPath);
        if (!extracted.Ok)
        {
            return new StreamingEnvOutcome(false, extracted.Evidence);
        }

        // 建屏设置必须先落位：驱动在设备启动时才读它，晚一步就得再重启一次设备。
        var seeded = StreamingEnvInstaller.TrySeedDriverSettings(Path.GetDirectoryName(infPath)!, out var settingsPath);

        // 先把驱动包放进驱动库，再创建根设备，PnP 才不会停在"没有驱动"（代码 28）。
        var added = await StreamingEnvInstaller.AddDriverAsync(infPath, token).ConfigureAwait(false);
        if (!added.Ok)
        {
            return new StreamingEnvOutcome(false, added.Evidence);
        }

        Say("驱动包已入库，正在创建虚拟显示根设备。");
        var created = VirtualDisplayDevice.TryCreate(infPath);
        return created.Present && created.Problem != VirtualDisplayDevice.ProbNoDriver
            ? new StreamingEnvOutcome(true, $"{seeded.Evidence}；{added.Evidence} {created.Evidence}（设置路径 {settingsPath}）")
            : new StreamingEnvOutcome(false, $"{seeded.Evidence}；{added.Evidence} {created.Evidence}");
    }

    /// <summary>
    /// "把虚拟屏接进桌面"的实际执行者。显示拓扑只能在交互会话里改，所以由宿主注入；
    /// 没注入时该步骤如实失败，而不是假装已经接好。
    /// </summary>
    public Func<CancellationToken, Task<StreamingEnvOutcome>>? AttachVirtualDisplay { get; set; }

    public async Task<StreamingEnvOutcome> EnsureVirtualDisplayInDesktopAsync(CancellationToken token)
    {
        var attach = AttachVirtualDisplay;
        if (attach is null)
        {
            return new StreamingEnvOutcome(false, "未接入交互会话，服务端无法把虚拟屏接进桌面布局。");
        }

        return await attach(token).ConfigureAwait(false);
    }

    public async Task<StreamingEnvOutcome> EnsureDriverSettingsAsync(CancellationToken token)
    {
        const string settingsTarget = @"C:\VirtualDisplayDriver\vdd_settings.xml";

        if (File.Exists(settingsTarget))
        {
            return new StreamingEnvOutcome(true, "驱动建屏设置已存在，未改动：" + settingsTarget);
        }

        // 优先用已经解出来的包；没有就取回钉死的 driver-only 包（132 KB，也支持离线目录）。
        var source = FindExtractedSettings();
        if (source is null)
        {
            var fetched = await _fetcher
                .FetchAsync(StreamingEnvAssets.VirtualDisplayDriver, OfflineDirectory, DownloadProgress("虚拟显示驱动"), token)
                .ConfigureAwait(false);
            if (!fetched.Ok)
            {
                return new StreamingEnvOutcome(false, "无法取回驱动包以放置建屏设置：" + fetched.Evidence);
            }

            var extracted = StreamingEnvInstaller.ExtractDriver(fetched.LocalPath, out _);
            if (!extracted.Ok)
            {
                return new StreamingEnvOutcome(false, extracted.Evidence);
            }

            source = FindExtractedSettings();
        }

        if (source is null)
        {
            return new StreamingEnvOutcome(false, "驱动包里没有 vdd_settings.xml，无法让驱动创建监视器");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsTarget)!);
            File.Copy(source, settingsTarget);
            return new StreamingEnvOutcome(true,
                "已放置驱动建屏设置（来自钉死包，未覆盖已有文件）：" + settingsTarget);
        }
        catch (Exception ex)
        {
            return new StreamingEnvOutcome(false, $"写入 {settingsTarget} 失败：{ex.Message}");
        }
    }

    private static string? FindExtractedSettings()
        => Directory.EnumerateFiles(Path.Combine(StreamingEnvCache.Directory, "vdd"), "vdd_settings.xml",
                SearchOption.AllDirectories).FirstOrDefault();

    public Task<StreamingEnvOutcome> SetVirtualDisplayEnabledAsync(bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var result = VirtualDisplayDevice.TrySetEnabled(enabled);
        return Task.FromResult(result.Present && result.Enabled == enabled
            ? new StreamingEnvOutcome(true, result.Evidence)
            : new StreamingEnvOutcome(false, result.Evidence));
    }

    public async Task<StreamingEnvOutcome> ApplySunshineConfigurationAsync(SunshineConfigMode mode, CancellationToken token)
    {
        var sun = SunshineEnvironmentProbe.Read();
        var editor = new SunshineConfigEditor(sun.ConfigFilePath);

        if (mode == SunshineConfigMode.Revert)
        {
            return editor.TryRevert(out var revertEvidence)
                ? new StreamingEnvOutcome(true, revertEvidence)
                : new StreamingEnvOutcome(false, revertEvidence);
        }

        if (!sun.ConfigFileExists)
        {
            return new StreamingEnvOutcome(false, $"Sunshine 配置文件不存在，无法写入：{sun.ConfigFilePath}");
        }

        var (guid, guidSource) = ResolveOutputGuid(sun, editor.ReadValues());
        if (guid is null)
        {
            // 新装驱动后 Sunshine 得重启一次才会把这块屏打进它的设备清单。
            Say("尚未确定虚拟屏的 output_name，正在让 Sunshine 重新枚举显示设备。");
            await RestartSunshineAsync(token).ConfigureAwait(false);
            (guid, guidSource) = await WaitForOutputGuidAsync(sun, editor, GuidWait, token).ConfigureAwait(false);
        }

        if (guid is null && mode == SunshineConfigMode.Activate)
        {
            // 实测卡点：物理屏形态下虚拟路径存在但不在桌面布局里，于是 Sunshine 的清单没有它、
            // 也就取不到 output_name，而 ensure_primary 恰恰是把它拉进桌面的那只手 —— 互锁与取值互相等。
            // 先做一件不碰物理屏的事：重启虚拟设备（禁用→启用），给驱动重建监视器对象的机会，
            // 再让 Sunshine 重新枚举一次。仍然取不到就照实拒绝，绝不猜一个标识写进配置。
            Say("仍未确定 output_name，正在重启虚拟显示设备以重建显示路径。");
            await SetVirtualDisplayEnabledAsync(false, token).ConfigureAwait(false);
            await Task.Delay(800, token).ConfigureAwait(false);
            await SetVirtualDisplayEnabledAsync(true, token).ConfigureAwait(false);
            await RestartSunshineAsync(token).ConfigureAwait(false);
            (guid, guidSource) = await WaitForOutputGuidAsync(sun, editor, GuidWait, token).ConfigureAwait(false);
        }

        if (guid is null)
        {
            return new StreamingEnvOutcome(false,
                "未能确定虚拟屏的 output_name（Sunshine 的设备清单里没有可判定为虚拟屏的输出），已拒绝写入「只留虚拟屏」配置。");
        }

        var applied = mode == SunshineConfigMode.Activate
            ? editor.TryApplyActivating(guid, out var writeEvidence)
            : editor.TryApplyEnabled(guid, out writeEvidence);

        return applied
            ? new StreamingEnvOutcome(true, $"{writeEvidence} 标识来自{guidSource}，写入形态={mode}。")
            : new StreamingEnvOutcome(false, writeEvidence);
    }

    public async Task<StreamingEnvOutcome> RestartSunshineAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(SunshineEnvironmentProbe.ServiceName);

            // 重启前先取日志尾部指纹。必须在 Stop 之前读：新版 Sunshine 会轮转日志
            // （sunshine.log -> .1 -> .2），重启后新文件从头写且启动块几秒内就写完——
            // 若在 WaitForStatus(Running) 之后才读，读到的是新块自身的指纹，
            // 等待循环里"尾部与重启前不同"这条判据永远不成立，必然白等 15 秒
            // （实测报"15 秒内未见新启动块"的根因）。
            var logPath = SunshineEnvironmentProbe.Read().LogFilePath;
            var hashBefore = string.Empty;
            try
            {
                hashBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(ReadLogTail(logPath, 64 * 1024))));
            }
            catch (IOException)
            {
            }

            if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            controller.Start();
            controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(40));

            // 等到新的启动块落盘再算完：生效值解析锚定启动块，立刻返回会让下一次探测
            // 读到上一次启动的旧生效形态（实测：恢复后四步全 Ok 却判 PartialFailure）。
            var settled = await WaitForStartupBlockAsync(logPath, hashBefore, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);

            return new StreamingEnvOutcome(true,
                $"{SunshineEnvironmentProbe.ServiceName} 已重启并进入 Running；"
                + (settled ? "新启动块已落盘。" : "但 15 秒内未见新启动块，生效值可能滞后。"));
        }
        catch (Exception ex)
        {
            return new StreamingEnvOutcome(false, $"服务重启失败：{ex.Message}");
        }
    }

    public async Task<StreamingEnvOutcome> RestorePhysicalDisplayAsync(CancellationToken token)
    {
        var restore = RestorePhysical;
        if (restore is null)
        {
            return new StreamingEnvOutcome(false,
                "未接入交互会话，服务端无法把物理屏设回主显：请在桌面端用紧急恢复键交还控制权。");
        }

        return await restore(token).ConfigureAwait(false);
    }

    /// <summary>
    /// 等到新的启动块完整落盘：Sunshine 的 config 行是逐行写的，块一出现就返回会读到半截
    /// （实测：启用七步全 Ok 却因 effective 缺键判 PartialFailure）。
    /// 判据用"日志静默"——连续两次读到相同的尾部哈希，且此时能解析出启动块。
    /// </summary>
    /// <summary>
    /// 等到"比重启前更长的"新启动块完整落盘。三条判据缺一不可：
    /// ① 文件比 <paramref name="minLength"/> 更长（新块必然追加字节，排除把旧块的静默当成新块落完）；
    /// ② 日志静默（连续两次读到相同的尾部哈希，排除半截块）；
    /// ③ 能按启动块锚定解析出配置行。
    /// </summary>
    private static async Task<bool> WaitForStartupBlockAsync(string logPath, string hashBefore, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var diag = Environment.GetEnvironmentVariable("GUARDIAN_STREAMING_DIAG") == "1";
        string? lastHash = null;
        var stable = 0;
        var i = 0;

        while (Environment.TickCount64 < deadline)
        {
            var hash = string.Empty;
            try
            {
                var tail = ReadLogTail(logPath, 64 * 1024);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(tail)));
            }
            catch (IOException)
            {
                // Sunshine 正在写：本轮不算静默，下一轮再看。
            }

            if (hash.Length > 0 && hash == lastHash)
            {
                stable++;
            }
            else
            {
                lastHash = hash;
                stable = 0;
            }

            var tryOk = SunshineEffectiveConfig.TryRead(logPath, out _, out var tryEvidence);

            // 三条判据：① 尾部已与重启前不同（新块已写出，轮转日志也成立）；② 静默；③ 可锚定解析。
            if (stable >= 2
                && hash.Length > 0
                && !string.Equals(hash, hashBefore, StringComparison.Ordinal)
                && tryOk)
            {
                return true;
            }

            if (diag)
            {
                Console.WriteLine($"[wfsb] {DateTime.Now:HH:mm:ss.fff} i={i} hash={(hash.Length == 0 ? "-" : hash[..8])} stable={stable} differs={(hash.Length > 0 && !string.Equals(hash, hashBefore, StringComparison.Ordinal))} tryOk={tryOk} ev={tryEvidence}");
            }

            await Task.Delay(400, token).ConfigureAwait(false);
            i++;
        }

        return false;
    }

    private static string ReadLogTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var take = (int)Math.Min(stream.Length, maxBytes);
        stream.Seek(stream.Length - take, SeekOrigin.Begin);
        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var got = stream.Read(buffer, read, take - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// 重启之后等清单落地。实测 Sunshine 从进入 Running 到把新的显示设备清单写进日志要十几秒
    /// （21:36:46 我立刻读→取不到；21:37:08 日志里已经有 2 台设备，虚拟屏成了 \\.\DISPLAY7）；
    /// 早先"重启完马上读"会把一次其实成功的自举误判成失败，所以这里轮询到有值或超时为止。
    /// </summary>
    private async Task<(string? Guid, string Source)> WaitForOutputGuidAsync(
        SunshineEnvironmentSnapshot sun,
        SunshineConfigEditor editor,
        TimeSpan timeout,
        CancellationToken token)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var resolved = ResolveOutputGuid(sun, editor.ReadValues());
            if (resolved.Guid is not null || Environment.TickCount64 >= deadline)
            {
                return resolved;
            }

            await Task.Delay(GuidPollInterval, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// output_name 的取值顺序：Sunshine 自己的设备清单（最可靠）→ 配置文件里已有的合法值。
    /// 两条都不成立时返回 null，由调用方决定"重启再看一次"还是"拒写"。
    /// </summary>
    private (string? Guid, string Source) ResolveOutputGuid(SunshineEnvironmentSnapshot sun, IReadOnlyDictionary<string, string> fileValues)
    {
        if (SunshineDisplayDeviceLog.TryReadLatest(sun.LogFilePath, out var listed, out _))
        {
            var found = SunshineDisplayDeviceLog.FindVirtual(listed, out _);
            if (found is not null && SunshineConfigEditor.IsPlausibleOutputName(found.DeviceId))
            {
                RememberOutputGuid?.Invoke(found.DeviceId.Trim());
                return (found.DeviceId.Trim(), "Sunshine 日志的显示设备清单");
            }
        }

        if (fileValues.TryGetValue(SunshineConfigEditor.KeyOutputName, out var existing)
            && SunshineConfigEditor.IsPlausibleOutputName(existing))
        {
            RememberOutputGuid?.Invoke(existing.Trim());
            return (existing.Trim(), "配置文件中的既有 output_name");
        }

        // 物理屏形态下虚拟屏常常"有路径但不在桌面"，上面两条都取不到值；
        // 这时沿用上次确认过的标识才有资格进入第一阶段（ensure_primary 不停物理屏，试错是安全的）。
        var lastKnown = LastKnownOutputGuid?.Invoke();
        if (SunshineConfigEditor.IsPlausibleOutputName(lastKnown))
        {
            return (lastKnown!.Trim(), "上次记在设置里的 output_name（不是本次当场命中）");
        }

        return (null, string.Empty);
    }

    /// <summary>运行中进程实际生效的形态：(是否接管中, 是否只激活)。日志没有这些键时按"不接管"处理。</summary>
    private static (bool Configured, bool Activating) ReadEffectiveForm(SunshineEnvironmentSnapshot sun)
    {
        if (!SunshineEffectiveConfig.TryRead(sun.LogFilePath, out var effective, out _))
        {
            return (false, false);
        }

        if (!effective.TryGetValue(SunshineConfigEditor.KeyOutputName, out var name)
            || !SunshineConfigEditor.IsPlausibleOutputName(name))
        {
            return (false, false);
        }

        if (!effective.TryGetValue(SunshineConfigEditor.KeyConfiguration, out var mode))
        {
            return (false, false);
        }

        if (mode.Equals(SunshineConfigEditor.EnsureOnlyDisplayValue, StringComparison.OrdinalIgnoreCase))
        {
            return (true, false);
        }

        return mode.Equals(SunshineConfigEditor.EnsurePrimaryValue, StringComparison.OrdinalIgnoreCase)
            ? (true, true)
            : (false, false);
    }

    private StreamingTopologyFact? FreshFact()
    {
        var fact = _topology();
        if (fact is null)
        {
            return null;
        }

        return DateTimeOffset.Now - fact.ReportedAt <= _topologyFreshness ? fact : null;
    }

    private IProgress<(long Downloaded, long Total)> DownloadProgress(string label) =>
        new Progress<(long Downloaded, long Total)>(value =>
        {
            var total = value.Total > 0 ? value.Total : 1;
            var percent = Math.Min(100, value.Downloaded * 100 / total);
            Say($"正在下载{label}：{percent}%（{FormatSize(value.Downloaded)}）");
        });

    /// <summary>已下载量的可读单位。原先固定用 MB，132 KB 的驱动包就显示成"0 MB"。</summary>
    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / 1024d / 1024d).ToString("0.#") + " MB"
            : Math.Max(1, bytes / 1024) + " KB";

    private void Say(string text)
    {
        Activity?.Invoke(text);
    }
}
