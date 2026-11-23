using System.Net.NetworkInformation;
using Guardian.Windows.Display;

namespace Guardian.Windows.Networking;

/// <summary>
/// 检测活跃的本地游戏串流会话（Sunshine/Moonlight）。
/// 双信号（任一命中即视为串流中）：
/// 1. Sunshine 默认使用 47989（HTTPS）/ 47990（HTTP）/ 47998-48010（视频）TCP 端口段，
///    任一端口存在 ESTABLISHED 连接。注意：Moonlight 视频走 UDP，TCP 控制连接在会话期间
///    并不持续，仅靠端口检测会在串流中途失检（实测：串流启动 2 秒后 TCP 即消失）。
/// 2. 物理显示器全部停用（虚拟显示器方案 ensure_only_display 的可靠拓扑信号）——
///    串流期间 Sunshine 只保留虚拟显示器，物理屏从活动拓扑中消失，该信号贯穿整个会话。
/// 检测失败时按“无串流”处理（信号 2 读取失败按“有物理屏”处理），不干扰关屏主流程。
/// </summary>
public sealed class StreamingSessionDetector
{
    /// <summary>47989..48010，覆盖 Sunshine 控制与视频流端口段。</summary>
    private static readonly int[] SunshineTcpPorts = Enumerable.Range(47989, 22).ToArray();

    public bool IsStreamingActive() => HasSunshineTcpConnection() || !DisplayTopology.HasPhysicalDisplay();

    private static bool HasSunshineTcpConnection()
    {
        try
        {
            var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            foreach (var connection in connections)
            {
                if (connection.State != TcpState.Established)
                {
                    continue;
                }
                if (Array.BinarySearch(SunshineTcpPorts, connection.LocalEndPoint.Port) >= 0
                    || Array.BinarySearch(SunshineTcpPorts, connection.RemoteEndPoint.Port) >= 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 网络查询失败时按“无串流”处理。
        }
        return false;
    }
}
