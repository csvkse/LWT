using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LinuxWebTool.Infrastructure.Shell;

/// <summary>
/// Shell 命令执行器：
/// - 命令模式：Linux 走 /bin/bash -c（目标环境），Windows 开发环境降级 cmd /c；
/// - 脚本模式：脚本正文写临时 .sh（UTF-8 / LF）后 `bash script.sh $1 $2...`，执行完删除临时文件；
///   Windows 自动探测 Git Bash。统一支持超时终止整个进程树、输出截断与并发上限（信号量排队）。
/// </summary>
public sealed class ShellExecutor : IShellExecutor
{
    private readonly ShellOptions _options;
    private readonly ILogger<ShellExecutor> _logger;
    private readonly SemaphoreSlim _gate;

    public ShellExecutor(ShellOptions options, ILogger<ShellExecutor> logger)
    {
        _options = options;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    public async Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
    {
        var timeoutSeconds = request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds;
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, Math.Max(1, _options.MaxTimeoutSeconds));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(request.ScriptText))
            {
                return await ExecuteScriptAsync(request, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            }
            return await ExecuteCommandAsync(request, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<ShellResult> ExecuteCommandAsync(ShellRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (fileName, _) = ResolveShell();
        var startInfo = CreateStartInfo(fileName, request.WorkingDirectory);

        // bash -c 与 cmd /s /c 都要求把整段命令作为一个参数传入。
        if (IsWindowsShell(fileName))
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(request.CommandText ?? string.Empty);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(request.CommandText ?? string.Empty);
        }

        return RunProcessAsync(startInfo, request.CommandText ?? string.Empty, timeout, cancellationToken);
    }

    private async Task<ShellResult> ExecuteScriptAsync(ShellRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bashPath = ResolveBash();
        if (bashPath is null)
        {
            _logger.LogError("脚本执行失败：未找到 bash（Linux 需 /bin/bash，Windows 开发环境需 Git Bash）");
            return new ShellResult
            {
                Started = false,
                StartFailure = "当前环境未找到 bash，无法执行脚本（Linux 原生支持；Windows 需安装 Git Bash）",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
            };
        }

        var scriptFile = Path.Combine(Path.GetTempPath(), $"lwt-script-{Guid.NewGuid():N}.sh");
        try
        {
            // 统一 LF 行尾（bash 对 CRLF 敏感），UTF-8 无 BOM。
            var normalized = (request.ScriptText ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            await File.WriteAllTextAsync(scriptFile, normalized, new UTF8Encoding(false), cancellationToken);

            var startInfo = CreateStartInfo(bashPath, request.WorkingDirectory);
            startInfo.ArgumentList.Add(scriptFile);
            foreach (var argument in ShellArgumentParser.Split(request.ScriptArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var summary = $"[脚本 {normalized.Length} 字符]" + (request.ScriptArguments is { Length: > 0 } ? $" 参数: {request.ScriptArguments}" : string.Empty);
            return await RunProcessAsync(startInfo, summary, timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "脚本写入临时文件失败");
            return new ShellResult
            {
                Started = false,
                StartFailure = "脚本写入临时文件失败：" + ex.Message,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
            };
        }
        finally
        {
            try
            {
                if (File.Exists(scriptFile))
                {
                    File.Delete(scriptFile);
                }
            }
            catch
            {
                // 临时文件清理失败不影响执行结果。
            }
        }
    }

    private ProcessStartInfo CreateStartInfo(string fileName, string? workingDirectory)
    {
        var directory = !string.IsNullOrWhiteSpace(workingDirectory)
            ? workingDirectory
            : _options.WorkingDirectory;
        return new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = ResolveOutputEncoding(),
            StandardErrorEncoding = ResolveOutputEncoding(),
            WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory,
        };
    }

    private async Task<ShellResult> RunProcessAsync(ProcessStartInfo startInfo, string commandForLog, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shell 启动失败：{Command}", commandForLog);
            return new ShellResult
            {
                Started = false,
                StartFailure = ex.Message,
                StartTime = startedAt,
                EndTime = DateTime.Now,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        // 读流不传 token：超时/取消杀掉进程后流自然关闭，保证尽量拿到已产出的输出。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        bool timedOut = false, cancelled = false;
        int? exitCode = null;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                exitCode = process.HasExited ? process.ExitCode : null;
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                TryKillTree(process);
            }
        }

        var stdout = await SafeAwaitAsync(stdoutTask);
        var stderr = await SafeAwaitAsync(stderrTask);
        stopwatch.Stop();

        var (outText, outTruncated) = Truncate(stdout, _options.MaxOutputBytes);
        var (errText, errTruncated) = Truncate(stderr, _options.MaxOutputBytes);

        if (timedOut)
        {
            _logger.LogWarning("指令执行超时已终止（{DurationMs}ms）：{Command}", stopwatch.ElapsedMilliseconds, commandForLog);
        }
        else
        {
            _logger.LogInformation("指令执行完成（{DurationMs}ms，exit {ExitCode}）：{Command}", stopwatch.ElapsedMilliseconds, exitCode, commandForLog);
        }

        return new ShellResult
        {
            ExitCode = exitCode,
            StandardOutput = outText,
            ErrorOutput = errText,
            DurationMs = stopwatch.ElapsedMilliseconds,
            TimedOut = timedOut,
            Cancelled = cancelled,
            Truncated = outTruncated || errTruncated,
            Started = true,
            StartTime = startedAt,
            EndTime = DateTime.Now,
        };
    }

    private static string? ResolveWorkingDirectory(string? requestDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestDirectory))
        {
            return requestDirectory;
        }
        return null; // 组合根已在 ShellOptions.WorkingDirectory 配置默认值，ProcessStartInfo 为空时继承进程目录
    }

    private static (string FileName, string Arguments) ResolveShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", string.Empty);
        }

        foreach (var shell in new[] { "/bin/bash", "/usr/bin/bash", "/bin/sh" })
        {
            if (File.Exists(shell))
            {
                return (shell, string.Empty);
            }
        }

        return ("/bin/sh", string.Empty);
    }

    /// <summary>脚本执行需要 bash：Linux 用系统 bash；Windows 探测 Git Bash 常见安装路径。</summary>
    private static string? ResolveBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : File.Exists("/usr/bin/bash") ? "/usr/bin/bash" : null;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsWindowsShell(string fileName) =>
        fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Linux 输出按 UTF-8；Windows 的 cmd 输出使用 OEM 代码页（zh-CN 下为 936），失败时回退 UTF-8。bash 输出始终 UTF-8。</summary>
    private static Encoding ResolveOutputEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UTF8Encoding(false);
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能恰好在杀掉前退出，忽略。
        }
    }

    private static (string Text, bool Truncated) Truncate(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, false);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var clipped = new byte[maxBytes];
        Array.Copy(bytes, clipped, maxBytes);
        var marker = Encoding.UTF8.GetBytes("\n…[输出已截断]…");
        var result = new byte[maxBytes + marker.Length];
        Array.Copy(clipped, result, maxBytes);
        Array.Copy(marker, 0, result, maxBytes, marker.Length);
        return (Encoding.UTF8.GetString(result), true);
    }
}
