using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Guardian.Windows.Streaming;

/// <summary>取到一个产物后的结果：本地路径与取证说明。</summary>
public sealed record FetchOutcome(bool Ok, string Evidence, string LocalPath);

/// <summary>Guardian 存放下载产物的目录（SYSTEM 服务写，普通用户不需要权限）。</summary>
public static class StreamingEnvCache
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Guardian",
        "streaming-env");

    public static string Prepare()
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Directory;
    }
}

/// <summary>
/// 钉死产物的取回器：本地缓存 → 离线目录 → 联网下载，三条路都要过 SHA256 才交出去。
/// 实测国内直连 GitHub release 资源约 214 KB/s，MSI 要 3 分钟，所以下载必须能报进度、能被取消，
/// 且用“读不到字节才算超时”的在途超时，不能用整请求超时（否则每次都会在半路被判死）。
/// </summary>
public sealed class AssetFetcher
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 连接与响应头阶段的超时。此前只有"读不到字节"的在途超时，而 GetAsync 本身没有时限：
    /// 新机直连 GitHub 时若 SYN 被黑洞，请求会永远挂着 —— 实测表现为一键启用停在"处理中"、
    /// 七步全"待执行"，而且因为超时没触发，谁也取消不了它。
    /// </summary>
    private static readonly TimeSpan ResponseHeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _client;

    public AssetFetcher(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <param name="offlineDirectory">离线兜底目录（可为空）。放同名文件即可，哈希不符不会被使用。</param>
    public async Task<FetchOutcome> FetchAsync(
        PinnedAsset asset,
        string? offlineDirectory = null,
        IProgress<(long Downloaded, long Total)>? progress = null,
        CancellationToken token = default)
    {
        var cache = StreamingEnvCache.Prepare();
        var target = Path.Combine(cache, asset.FileName);

        if (Matches(target, asset))
        {
            return new FetchOutcome(true, $"已有缓存且哈希一致：{asset.FileName}（{asset.Version}）。", target);
        }

        if (!string.IsNullOrEmpty(offlineDirectory))
        {
            var offline = Path.Combine(offlineDirectory, asset.FileName);
            if (File.Exists(offline))
            {
                if (!Matches(offline, asset))
                {
                    return new FetchOutcome(false,
                        $"离线文件 {asset.FileName} 哈希与钉死值不符（期望 {asset.Sha256Short}…，实际 {Hash(offline)}），已放弃使用。",
                        target);
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Copy(offline, target);
                return new FetchOutcome(true, $"已从离线目录取用 {asset.FileName}（{asset.Version}），哈希一致。", target);
            }
        }

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new FetchOutcome(false, $"钉死地址不是 HTTPS，拒绝下载：{asset.Url}", target);
        }

        var partial = target + ".part";
        var done = 0L;
        try
        {
            using var response = await GetWithHeaderTimeoutAsync(uri, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new FetchOutcome(false, $"下载失败：HTTP {(int)response.StatusCode}（{asset.FileName}）。", target);
            }

            var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                var reportClock = Stopwatch.StartNew();

                while (true)
                {
                    // 在途超时：一段时间读不到字节就认定链路卡死，主动断开并如实报告。
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                    linked.CancelAfter(InactivityTimeout);

                    var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;

                    if (done >= total || reportClock.Elapsed >= ProgressThrottle)
                    {
                        reportClock.Restart();
                        progress?.Report((done, total));
                    }
                }
            }

            token.ThrowIfCancellationRequested();

            if (!Matches(partial, asset))
            {
                File.Delete(partial);
                return new FetchOutcome(false,
                    $"下载完成但哈希不符，已删除：期望 {asset.Sha256Short}…，实际 {Hash(partial)}。",
                    target);
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(partial, target);
            return new FetchOutcome(true,
                $"已下载 {asset.FileName}（{asset.Version}，{Format(done)}），SHA256 校验通过。",
                target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            return new FetchOutcome(false, $"下载已取消，部分文件已删除：{asset.FileName}。", target);
        }
        catch (TimeoutException ex)
        {
            TryDelete(partial);
            return new FetchOutcome(false, $"下载未开始：{ex.Message}", target);
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            // 失败结论里必须带上可执行的下一步：新机常常根本连不上 GitHub，
            // 只报"下载失败"等于让用户去猜怎么办。
            var hint = string.IsNullOrEmpty(offlineDirectory)
                ? "可在 设置 → 安装包离线目录 填入放有 " + asset.FileName + " 的文件夹后重试。"
                : "离线目录 " + offlineDirectory + " 里没有这个文件（或文件名不一致）。";
            return new FetchOutcome(false, $"下载失败：{ex.Message}（{asset.FileName}）。{hint}", target);
        }
    }

    /// <summary>
    /// 只给"连接 + 响应头"阶段设时限。这一步没有字节可读，在途超时管不到它；
    /// 不设限就会出现实测见过的一次：新机直连 GitHub 时请求永远挂着，一键启用停在"处理中"、
    /// 七步全"待执行"，且没有任何东西能取消它。
    /// </summary>
    private async Task<HttpResponseMessage> GetWithHeaderTimeoutAsync(Uri uri, CancellationToken token)
    {
        using var headerSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        headerSource.CancelAfter(ResponseHeaderTimeout);

        try
        {
            return await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headerSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{ResponseHeaderTimeout.TotalSeconds:0} 秒内没拿到 {uri.Host} 的响应头（链路可能被拦截或黑洞）。"
                + "把官方安装包放进离线目录后重试即可绕开。");
        }
    }

    public static bool Matches(string path, PinnedAsset asset)
        => File.Exists(path) && string.Equals(Hash(path), asset.Sha256, StringComparison.OrdinalIgnoreCase);

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Format(long bytes)
        => (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉只留下一个 .part，不影响结论。
        }
    }
}
