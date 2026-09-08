using System.Diagnostics;
using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>
/// ffmpeg / ffprobe 可用性检测与解析。默认走 PATH；可经 Media:FfmpegPath 配置绝对路径。
/// 检测结果缓存 60 秒，避免页面轮询反复拉起子进程。
/// </summary>
public sealed class FfmpegLocator(TranscodeOptions options)
{
    private (DateTime Time, FfmpegDetection Result)? _cache;

    public async Task<FfmpegDetection> DetectAsync()
    {
        if (_cache is { } cache && (DateTime.Now - cache.Time).TotalSeconds < 60)
        {
            return cache.Result;
        }

        var result = await DetectCoreAsync();
        _cache = (DateTime.Now, result);
        return result;
    }

    private async Task<FfmpegDetection> DetectCoreAsync()
    {
        var (exitCode, stdout) = await RunVersionAsync(options.FfmpegPath, "ffmpeg");
        if (exitCode != 0)
        {
            return new FfmpegDetection
            {
                Available = false,
                FfmpegPath = options.FfmpegPath,
                FfprobePath = options.FfprobePath,
                Message = "未检测到 ffmpeg。Docker 镜像已内置；桌面部署请安装 ffmpeg（apt install ffmpeg / brew / winget）"
                    + "或配置 Media__FfmpegPath 指向可执行文件",
            };
        }

        // ffprobe 用于时长探测；缺失只影响进度百分比，不阻断转码
        var (probeExit, probeOut) = await RunVersionAsync(options.FfprobePath, "ffprobe");
        var version = stdout.Split('\n').FirstOrDefault()?.Trim() ?? "ffmpeg";
        return new FfmpegDetection
        {
            Available = true,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = probeExit == 0 ? options.FfprobePath : string.Empty,
            Version = version,
            Message = probeExit != 0 ? "ffprobe 不可用（进度将无法显示百分比，转码本身不受影响）" : string.Empty,
        };
    }

    private static async Task<(int ExitCode, string Stdout)> RunVersionAsync(string fileName, string toolName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }
            var stdout = await process.StandardOutput.ReadToEndAsync();
            return (process.HasExited ? process.ExitCode : -1, stdout);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
