using System.Windows;
using Guardian.App.ViewModels;

namespace Guardian.App.Services;

/// <summary>
/// 以 1 秒周期在后台线程轮询服务状态，驱动主视图模型；连续失败判定为断开。
/// 心跳必须放在后台线程：WPF 的 DispatcherTimer 依赖 UI 线程消息循环，
/// 串流启动时显示拓扑切换（Sunshine 虚拟显示器接管）可能让 UI 线程卡顿或进程异常，
/// 心跳一旦中断，服务端会误判“客户端丢失”并在 15 秒后自动解除保护——这正是
/// “屏幕黑着、界面却显示未启动”的根因之一。后台心跳与 UI 线程解耦后，
/// 只有进程真正退出才会触发 fail-open 恢复。
/// </summary>
public sealed class ServiceConnectionMonitor
{
    private const int FailureThreshold = 3;
    private const int PollIntervalMs = 1000;

    private readonly GuardianClientService _client;
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _failures;
    private bool _wasLost;
    private int _pendingUiUpdate;

    public ServiceConnectionMonitor(GuardianClientService client, MainViewModel viewModel)
    {
        _client = client;
        _viewModel = viewModel;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _loop = Task.Run(LoopAsync);
    }

    public void Stop() => _cts.Cancel();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _client.PingAsync();
                if (snapshot is null)
                {
                    throw new InvalidOperationException("empty snapshot");
                }
                if (_wasLost)
                {
                    App.DiagLog("service connection restored");
                }
                _failures = 0;
                _wasLost = false;
                RunOnUi(() => _viewModel.ApplySnapshot(snapshot));
            }
            catch (Exception ex)
            {
                _failures++;
                if (_failures >= FailureThreshold && !_wasLost)
                {
                    _wasLost = true;
                    App.DiagLog($"service connection lost: {ex.Message}");
                    RunOnUi(_viewModel.MarkDisconnected);
                }
                else if (_failures >= FailureThreshold)
                {
                    RunOnUi(_viewModel.MarkDisconnected);
                }
            }

            try
            {
                await Task.Delay(PollIntervalMs, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 把状态更新调度回 UI 线程；若已有一个更新排队则合并（丢弃本次），
    /// 避免 UI 线程短暂卡顿后积压大量过期快照。
    /// </summary>
    private void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // 应用已退出，丢弃本次更新。
            return;
        }
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (Interlocked.Exchange(ref _pendingUiUpdate, 1) == 0)
        {
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _pendingUiUpdate, 0);
                action();
            });
        }
    }
}
