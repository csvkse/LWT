using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

/// <summary>
/// 扩展仓库拉取策略契约。
/// </summary>
public interface IExtensionRepoFetcher
{
    Task<(bool Success, string Message, string? LocalRepoDir)> FetchRepositoryAsync(
        string repositoryUrl,
        string? branch,
        string? proxyPrefix,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 组合式扩展仓库拉取器：
/// 1. 优先使用 Git CLI 浅克隆 (git clone --depth 1)；
/// 2. 若 Git CLI 不可用或克隆失败，自动降级至 HTTP Tarball 原生流式解压 (.NET 10 System.Formats.Tar)；
/// 3. 支持国内代理镜像源 (如 https://ghproxy.net/)。
/// </summary>
public sealed class CompositeExtensionRepoFetcher : IExtensionRepoFetcher
{
    private readonly IShellExecutor _shellExecutor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CompositeExtensionRepoFetcher> _logger;

    public CompositeExtensionRepoFetcher(
        IShellExecutor shellExecutor,
        ILogger<CompositeExtensionRepoFetcher> logger,
        HttpClient? httpClient = null)
    {
        _shellExecutor = shellExecutor;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public async Task<(bool Success, string Message, string? LocalRepoDir)> FetchRepositoryAsync(
        string repositoryUrl,
        string? branch,
        string? proxyPrefix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return (false, "仓库地址不能为空", null);
        }

        var trimmedUrl = repositoryUrl.Trim();
        var targetBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();

        // 规范化 GitHub 简写如 owner/repo
        var (owner, repo, fullGitUrl) = ParseGitUrl(trimmedUrl);

        // 应用镜像代理
        var effectiveGitUrl = fullGitUrl;
        if (!string.IsNullOrWhiteSpace(proxyPrefix))
        {
            var pfx = proxyPrefix.Trim().TrimEnd('/') + "/";
            if (!effectiveGitUrl.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                effectiveGitUrl = pfx + effectiveGitUrl;
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_repos", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // 策略 1: 尝试 Git CLI 浅克隆
        var (gitSuccess, gitMsg) = await TryGitCloneAsync(effectiveGitUrl, targetBranch, tempDir, cancellationToken);
        if (gitSuccess)
        {
            _logger.LogInformation("Successfully cloned repo {Url} via Git CLI into {Dir}", trimmedUrl, tempDir);
            return (true, "克隆成功", tempDir);
        }

        _logger.LogWarning("Git clone failed ({Msg}), attempting HTTP Tarball fallback...", gitMsg);

        // 策略 2: 降级至 HTTP Tarball 原生流式解压
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
        {
            var (tarSuccess, tarMsg) = await TryDownloadTarballAsync(owner, repo, targetBranch, proxyPrefix, tempDir, cancellationToken);
            if (tarSuccess)
            {
                _logger.LogInformation("Successfully downloaded and extracted tarball for {Owner}/{Repo} into {Dir}", owner, repo, tempDir);
                return (true, "下载解压成功", tempDir);
            }

            // 如果指定了 main 失败，尝试 fallback 分支 master
            if (targetBranch == "main")
            {
                var (masterSuccess, masterMsg) = await TryDownloadTarballAsync(owner, repo, "master", proxyPrefix, tempDir, cancellationToken);
                if (masterSuccess)
                {
                    return (true, "下载解压成功 (master 分支)", tempDir);
                }
            }

            CleanDir(tempDir);
            return (false, $"拉取仓库失败 (Git 错误: {gitMsg}；HTTP 归档错误: {tarMsg})", null);
        }

        CleanDir(tempDir);
        return (false, $"拉取仓库失败: {gitMsg}", null);
    }

    private async Task<(bool Success, string Message)> TryGitCloneAsync(string gitUrl, string branch, string targetDir, CancellationToken cancellationToken)
    {
        try
        {
            var cmd = $"git clone --depth 1 -b {branch} \"{gitUrl}\" \"{targetDir}\"";
            var res = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                CommandText = cmd,
                TimeoutSeconds = 35
            }, cancellationToken);

            if (res.ExitCode == 0 && Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                return (true, "OK");
            }

            return (false, string.IsNullOrWhiteSpace(res.ErrorOutput) ? res.StandardOutput : res.ErrorOutput);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Message)> TryDownloadTarballAsync(
        string owner,
        string repo,
        string branch,
        string? proxyPrefix,
        string targetDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.tar.gz";
            if (!string.IsNullOrWhiteSpace(proxyPrefix))
            {
                rawUrl = proxyPrefix.Trim().TrimEnd('/') + "/" + rawUrl;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
            request.Headers.Add("User-Agent", "LinuxWebTool-ExtensionImporter/1.0");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP 响应码: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzipStream = new GZipStream(netStream, CompressionMode.Decompress);

            // 使用 .NET 10 内置 System.Formats.Tar 流式解压
            await TarFile.ExtractToDirectoryAsync(gzipStream, targetDir, overwriteFiles: true, cancellationToken);

            if (Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                return (true, "OK");
            }

            return (false, "归档文件解压后目录为空");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (string? Owner, string? Repo, string FullUrl) ParseGitUrl(string url)
    {
        // 匹配 owner/repo
        var shortMatch = Regex.Match(url, @"^([a-zA-Z0-9_\-\.]+)/([a-zA-Z0-9_\-\.]+)$");
        if (shortMatch.Success)
        {
            var owner = shortMatch.Groups[1].Value;
            var repo = shortMatch.Groups[2].Value.Replace(".git", "");
            return (owner, repo, $"https://github.com/{owner}/{repo}.git");
        }

        // 匹配 https://github.com/owner/repo(.git)
        var httpMatch = Regex.Match(url, @"github\.com[/:]([a-zA-Z0-9_\-\.]+)/([a-zA-Z0-9_\-\.]+?)(?:\.git|/)?$");
        if (httpMatch.Success)
        {
            var owner = httpMatch.Groups[1].Value;
            var repo = httpMatch.Groups[2].Value;
            return (owner, repo, url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url : url + ".git");
        }

        return (null, null, url);
    }

    private static void CleanDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { }
    }
}
