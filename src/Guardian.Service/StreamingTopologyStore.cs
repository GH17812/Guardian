using Guardian.Contracts.Models;

namespace Guardian.Service;

/// <summary>
/// 交互会话拓扑上报的服务端留存。
/// 存在的理由是一个硬事实：Session 0 里 EnumDisplayMonitors 枚举不到任何屏幕，
/// "虚拟屏是不是真成了一块可用输出""物理屏有没有被夺回主显"这两件事只有客户端知道。
/// 因此服务端只存"客户端什么时候说了什么"，并按新鲜度决定信不信。
/// </summary>
public sealed class StreamingTopologyStore
{
    private readonly object _gate = new();

    private StreamingTopologyFact? _latest;
    private bool _restorePending;
    private DateTimeOffset _restoreRequestedAt;
    private bool _attachPending;
    private DateTimeOffset _attachRequestedAt;

    public void Publish(StreamingTopologyFact fact)
    {
        lock (_gate)
        {
            // 只接受比已存记录更新的上报，防止乱序到达的旧上报顶掉新状态。
            if (_latest is null || fact.ReportedAt >= _latest.ReportedAt)
            {
                _latest = fact;
            }
        }
    }

    public StreamingTopologyFact? Current()
    {
        lock (_gate)
        {
            return _latest;
        }
    }

    public bool RestorePending
    {
        get
        {
            lock (_gate)
            {
                return _restorePending;
            }
        }
    }

    /// <summary>服务端已请求客户端"把虚拟屏接进桌面"，等待交互会话执行后回执。</summary>
    public bool VirtualAttachPending
    {
        get
        {
            lock (_gate)
            {
                return _attachPending;
            }
        }
    }

    public void RequestVirtualAttach()
    {
        lock (_gate)
        {
            _attachPending = true;
            _attachRequestedAt = DateTimeOffset.Now;
        }
    }

    /// <summary>轮询等待"请求之后"的、且确认虚拟屏已在桌面布局里的上报。</summary>
    public async Task<bool> WaitForVirtualEnumeratedAsync(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            var deadline = DateTimeOffset.Now + timeout;
            while (DateTimeOffset.Now < deadline)
            {
                token.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (_latest is not null
                        && _latest.ReportedAt >= _attachRequestedAt
                        && _latest.VirtualDisplayEnumerated)
                    {
                        _attachPending = false;
                        return true;
                    }
                }

                await Task.Delay(200, token).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            // 成功、超时还是取消，都必须清掉待办：留着它会让客户端在一键启用结束之后
            // 仍然每 3 秒重试一次接屏（旧版实测：启用早已结束，接屏尝试还在无限循环，
            // 每次都动一次显示拓扑）。
            lock (_gate)
            {
                _attachPending = false;
            }
        }
    }

    /// <summary>登记一次"请把物理屏夺回主显"的请求，等待客户端执行后回执。</summary>
    public void RequestPhysicalRestore()
    {
        lock (_gate)
        {
            _restorePending = true;
            _restoreRequestedAt = DateTimeOffset.Now;
        }
    }

    public void CompletePhysicalRestore()
    {
        lock (_gate)
        {
            _restorePending = false;
        }
    }

    /// <summary>轮询等待一次"请求之后"的上报；超时返回 false，由调用方如实报未确认。</summary>
    public async Task<bool> WaitForRestoreAcknowledgedAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            token.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (!_restorePending)
                {
                    return true;
                }

                if (_latest is not null && _latest.ReportedAt >= _restoreRequestedAt && _latest.PhysicalDisplayActive)
                {
                    _restorePending = false;
                    return true;
                }
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        return false;
    }
}
