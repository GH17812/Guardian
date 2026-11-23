using Guardian.Contracts.Models;
using Guardian.Windows.Display;

namespace Guardian.App.Services;

/// <summary>
/// 客户端侧的串流环境轮询器：
/// 1. 定期把交互会话的显示器拓扑报给服务端（服务端在 Session 0，自己看不到屏幕）；
/// 2. 顺带回取最新环境档案，供界面渲染四态与分步进度；
/// 3. 档案上的 PhysicalRestorePending 是服务端交给本机执行的活——把物理屏重新挂回主显。
/// 管道是请求/应答模型、没有服务端推送通道，所以"下发任务"只能靠档案标志 + 客户端轮询完成。
/// </summary>
public sealed class StreamingEnvWatcher : IAsyncDisposable
{
    private const int IntervalMs = 3000;

    private readonly GuardianClientService _client;
    private readonly Func<Task<string?>> _restorePhysical;
    private readonly Func<Task<string?>> _attachVirtual;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private long _lastStampMs;
    private int _restoreInFlight;
    private int _attachInFlight;

    public StreamingEnvWatcher(
        GuardianClientService client,
        Func<Task<string?>> restorePhysical,
        Func<Task<string?>> attachVirtual)
    {
        _client = client;
        _restorePhysical = restorePhysical;
        _attachVirtual = attachVirtual;
    }

    public StreamingEnvironmentProfile? Latest { get; private set; }

    /// <summary>档案更新（后台线程回调，订阅方自行切回 UI 线程）。</summary>
    public event Action<StreamingEnvironmentProfile>? ProfileChanged;

    public event Action<string>? Diagnostics;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>立即往返一次（点完按钮后界面要马上看到进度，而不是等下一个周期）。</summary>
    public async Task<StreamingEnvironmentProfile?> PollOnceAsync(CancellationToken token = default)
    {
        var facts = SessionDisplayFactsReader.Read();
        var profile = await _client.ReportStreamingTopologyAsync(new StreamingTopologyPayload
        {
            VirtualDisplayEnumerated = facts.VirtualDisplayEnumerated,
            VirtualDisplayPathActive = facts.VirtualDisplayPathActive,
            PhysicalDisplayActive = facts.PhysicalDisplayActive,
            ActiveMonitorCount = facts.ActiveMonitorCount,
        });

        if (profile is null)
        {
            return Latest;
        }

        // 迟到档案丢弃：与快照同机制，屏幕正在被改的时候旧状态绝不能覆盖新状态。
        if (profile.StampMs != 0 && profile.StampMs < _lastStampMs)
        {
            return Latest;
        }

        _lastStampMs = profile.StampMs;
        Latest = profile;
        ProfileChanged?.Invoke(profile);
        return profile;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var profile = await PollOnceAsync(token).ConfigureAwait(false);
                if (profile is { PhysicalRestorePending: true })
                {
                    await TryRestorePhysicalAsync(token).ConfigureAwait(false);
                }
                else if (profile is { AttachVirtualDisplayPending: true })
                {
                    // 服务端请求把虚拟屏接进桌面：显示拓扑只能由交互会话改，
                    // 与"夺回物理主显"同一套机制，只是目标换成虚拟屏。
                    await TryAttachVirtualAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 服务没起来或管道断了：保留上一次档案，界面据此显示"未确认"而不是"正常"。
                Diagnostics?.Invoke($"串流环境拓扑上报失败：{ex.Message}");
            }

            try
            {
                await Task.Delay(IntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TryAttachVirtualAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _attachInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Diagnostics?.Invoke("服务端请求把虚拟屏接进桌面布局。");
            var evidence = await _attachVirtual().ConfigureAwait(false);
            Diagnostics?.Invoke(evidence ?? "虚拟屏接屏已执行。");

            // 马上回执，别让服务端等到下一个周期才确认。
            await PollOnceAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"虚拟屏接屏失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _attachInFlight, 0);
        }
    }

    private async Task TryRestorePhysicalAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _restoreInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Diagnostics?.Invoke("服务端请求把物理屏夺回主显，开始重新挂载。");
            var evidence = await _restorePhysical().ConfigureAwait(false);
            Diagnostics?.Invoke(evidence ?? "物理屏重新挂载已执行。");

            // 马上回执，别让服务端等到下一个周期才确认（它只在有限时间内等回执）。
            await PollOnceAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"物理屏重新挂载失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _restoreInFlight, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // 收尾忽略
            }
        }

        _cts.Dispose();
    }
}
