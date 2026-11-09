using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 原生子进程受控执行器：
/// - 使用 ArgumentList 直接传参，不经过任何 Shell 解析；
/// - 标准输入支持 closed/text/json/file 明确模式并在写入后关闭 EOF；
/// - 标准输出与错误流有界采集 (最大 256 KiB)，超出截断并持续排空管道，避免进程死锁；
/// - 超时或人工取消时级联终止进程树，并准确标记 EffectUncertain 状态。
/// </summary>
public sealed class NativeProcessRunner : INativeProcessRunner
{
    private const int MaxOutputBytes = 256 * 1024; // 256 KiB
    private readonly string _progressParser;

    public NativeProcessRunner(string progressParser = "none")
    {
        _progressParser = progressParser;
    }

    public async Task<NativeProcessResult> RunAsync(
        NativeProgramSpec spec,
        ICommandProgressObserver? observer,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in spec.ArgumentList)
        {
            psi.ArgumentList.Add(arg);
        }

        PluginEnvironmentSanitizer.ApplySafeEnvironment(psi, spec.EnvironmentVariables);

        if (File.Exists(spec.ExecutablePath))
        {
            var (isValid, reason) = NativeBinaryValidator.ValidateExecutable(spec.ExecutablePath);
            if (!isValid)
            {
                return new NativeProcessResult
                {
                    Started = false,
                    ExitCode = -1,
                    StandardError = $"原生程序二进制格式校验失败: {reason}"
                };
            }
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new NativeProcessResult
                {
                    Started = false,
                    ExitCode = -1,
                    StandardError = "无法启动可执行进程。"
                };
            }
        }
        catch (Exception ex)
        {
            return new NativeProcessResult
            {
                Started = false,
                ExitCode = -1,
                StandardError = $"进程启动失败: {ex.Message}"
            };
        }

        // 处理标准输入 (Stdin)
        var stdinTask = HandleStdinAsync(process, spec, ct);

        // 并发读取标准输出与错误，同时进行进度解析与有界缓冲
        var stdoutTask = ReadBoundedStreamWithProgressAsync(process.StandardOutput, observer, _progressParser, ct);
        var stderrTask = ReadBoundedStreamAsync(process.StandardError, ct);

        try
        {
            await Task.WhenAll(stdinTask, stdoutTask, stderrTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new NativeProcessResult
            {
                Started = true,
                ExitCode = process.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr,
                TimedOut = false,
                Cancelled = false,
                EffectUncertain = false
            };
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);

            string? stdoutPartial = null;
            string? stderrPartial = null;
            try { stdoutPartial = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : null; } catch { }
            try { stderrPartial = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : null; } catch { }

            return new NativeProcessResult
            {
                Started = true,
                ExitCode = -1,
                StandardOutput = stdoutPartial,
                StandardError = stderrPartial,
                Cancelled = true,
                EffectUncertain = true
            };
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);

            return new NativeProcessResult
            {
                Started = true,
                ExitCode = -1,
                StandardError = $"进程执行异常: {ex.Message}",
                EffectUncertain = true
            };
        }
    }

    private static async Task HandleStdinAsync(Process process, NativeProgramSpec spec, CancellationToken ct)
    {
        try
        {
            switch (spec.StdinMode.ToLowerInvariant())
            {
                case "closed":
                    process.StandardInput.Close();
                    break;

                case "text":
                case "json":
                    if (!string.IsNullOrEmpty(spec.StdinPayload))
                    {
                        await process.StandardInput.WriteAsync(spec.StdinPayload.AsMemory(), ct).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                    }
                    process.StandardInput.Close();
                    break;

                case "file":
                    if (!string.IsNullOrEmpty(spec.StdinFilePath) && File.Exists(spec.StdinFilePath))
                    {
                        await using var fs = new FileStream(spec.StdinFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
                        await fs.CopyToAsync(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
                        await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    process.StandardInput.Close();
                    break;

                default:
                    process.StandardInput.Close();
                    break;
            }
        }
        catch
        {
            try { process.StandardInput.Close(); } catch { }
        }
    }

    private static async Task<string> ReadBoundedStreamWithProgressAsync(
        StreamReader reader,
        ICommandProgressObserver? observer,
        string progressParser,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var bytesRead = 0;
        var truncated = false;
        var buffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;

            if (observer != null && progressParser != "none")
            {
                var (hasProgress, pct, msg) = CommandParsers.ParseProgressLine(progressParser, line);
                if (hasProgress)
                {
                    try { await observer.OnProgressAsync(pct, msg, ct).ConfigureAwait(false); } catch { }
                }
            }

            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
            if (!truncated)
            {
                if (bytesRead + lineBytes <= MaxOutputBytes)
                {
                    sb.AppendLine(line);
                    bytesRead += lineBytes;
                }
                else
                {
                    sb.AppendLine(line);
                    sb.AppendLine($"\n... [TRUNCATED: Output exceeded {MaxOutputBytes / 1024} KiB limit] ...");
                    truncated = true;
                }
            }
        }

        return sb.ToString();
    }

    private static async Task<string> ReadBoundedStreamAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var bytesRead = 0;
        var truncated = false;
        var buffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;

            var charBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (!truncated)
            {
                if (bytesRead + charBytes <= MaxOutputBytes)
                {
                    sb.Append(buffer, 0, read);
                    bytesRead += charBytes;
                }
                else
                {
                    var allowedChars = (MaxOutputBytes - bytesRead) / 2;
                    if (allowedChars > 0 && allowedChars <= read)
                    {
                        sb.Append(buffer, 0, allowedChars);
                    }
                    sb.AppendLine($"\n... [TRUNCATED: Output exceeded {MaxOutputBytes / 1024} KiB limit] ...");
                    truncated = true;
                }
            }
        }

        return sb.ToString();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略进程结束竞争
        }
    }
}
