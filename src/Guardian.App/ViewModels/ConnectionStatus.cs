namespace Guardian.App.ViewModels;

/// <summary>与服务端的连接状态。</summary>
public enum ConnectionStatus
{
    /// <summary>已连接。</summary>
    Connected,

    /// <summary>服务未安装。</summary>
    ServiceNotInstalled,

    /// <summary>服务已安装但未运行。</summary>
    ServiceNotRunning,

    /// <summary>服务运行中但无法建立管道连接。</summary>
    Disconnected,
}
