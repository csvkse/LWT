using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

public sealed class CrossPlatformPtyEngine : IPtyEngine
{
    public bool IsSupported => true;

    public Task<IPtySession> StartSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = Guid.NewGuid().ToString("N");
        var (shell, args) = ResolveShell(options.Executable, options.Arguments);

        if (LinuxNativePty.IsSupported)
            return Task.FromResult(LinuxNativePty.Start(sessionId, shell, args, options));

        if (OperatingSystem.IsWindows() && WindowsConPtyFactory.IsSupported)
        {
            var conPtySession = WindowsConPtyFactory.TryCreateSession(
                sessionId,
                shell,
                args,
                options.WorkingDirectory,
                options.Columns > 0 ? options.Columns : 80,
                options.Rows > 0 ? options.Rows : 24,
                options.EnvironmentVariables);

            if (conPtySession is not null)
            {
                return Task.FromResult(conPtySession);
            }
        }

        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory))
            startInfo.WorkingDirectory = options.WorkingDirectory;

        startInfo.EnvironmentVariables["TERM"] = "xterm-256color";
        startInfo.EnvironmentVariables["COLORTERM"] = "truecolor";
        startInfo.EnvironmentVariables["LINES"] = (options.Rows > 0 ? options.Rows : 24).ToString();
        startInfo.EnvironmentVariables["COLUMNS"] = (options.Columns > 0 ? options.Columns : 80).ToString();

        if (OperatingSystem.IsWindows())
        {
            startInfo.EnvironmentVariables["PROMPT"] = "$E[90m$P$E[0m$_$E[32m$G$E[0m ";
        }
        else
        {
            startInfo.EnvironmentVariables["PS1"] = @"\e[90m\w\e[0m\n\e[32m❯\e[0m ";
        }

        if (options.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in options.EnvironmentVariables)
                startInfo.EnvironmentVariables[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed_to_start_terminal_process");

        var session = new ProcessPtySession(sessionId, process);
        return Task.FromResult<IPtySession>(session);
    }

    private static (string Executable, IReadOnlyList<string> Arguments) ResolveShell(string? explicitExe, IReadOnlyList<string>? explicitArgs)
    {
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return (explicitExe, explicitArgs ?? []);

        if (OperatingSystem.IsWindows())
        {
            // Two-line prompt for modern terminal UX with UTF-8 encoding support:
            // Line 1: current location in muted gray (\e[90m)
            // Line 2: green ❯ symbol (\e[32m) with dedicated space for typing
            const string psPromptCommand = "$OutputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); function global:prompt { $p = (Get-Location).Path; $report = ''; if ((Get-Location).Provider.Name -eq 'FileSystem') { $report = [char]27 + ']7;' + ([uri]$p).AbsoluteUri + [char]7; if (!(Get-Job | Where-Object State -in @('Running','NotStarted','Blocked'))) { $report += [char]27 + ']133;A' + [char]7 } }; $report + [char]27 + '[90m' + $p + [char]27 + '[0m' + [char]13 + [char]10 + [char]27 + '[32m❯' + [char]27 + '[0m ' }";

            var pwsh = FindExecutableInPath("pwsh.exe") ?? FindWellKnownPowerShell("pwsh.exe");
            if (!string.IsNullOrWhiteSpace(pwsh))
                return (pwsh, ["-NoLogo", "-NoExit", "-Command", psPromptCommand]);

            var powershell = FindExecutableInPath("powershell.exe") ?? FindWellKnownPowerShell("powershell.exe");
            if (!string.IsNullOrWhiteSpace(powershell))
                return (powershell, ["-NoLogo", "-NoExit", "-Command", psPromptCommand]);

            var comspec = Environment.GetEnvironmentVariable("COMSPEC");
            if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
                return (comspec, ["/K", "chcp 65001 >nul & prompt $E[90m$P$E[0m$_$E[32m$G$E[0m "]);

            return ("cmd.exe", ["/K", "chcp 65001 >nul & prompt $E[90m$P$E[0m$_$E[32m$G$E[0m "]);
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell) && File.Exists(shell))
            return (shell, DefaultLinuxArguments(shell));

        if (File.Exists("/bin/bash"))
            return ("/bin/bash", DefaultLinuxArguments("/bin/bash"));

        if (File.Exists("/usr/bin/bash"))
            return ("/usr/bin/bash", DefaultLinuxArguments("/usr/bin/bash"));

        return ("/bin/sh", ["-i"]);
    }

    private static IReadOnlyList<string> DefaultLinuxArguments(string shell)
    {
        var rc = Path.Combine(AppContext.BaseDirectory, "terminal-bashrc.sh");
        return Path.GetFileName(shell) == "bash" && File.Exists(rc) ? ["--rcfile", rc, "-i"] : ["-i"];
    }

    private static string? FindExecutableInPath(string fileName)
    {
        if (File.Exists(fileName)) return Path.GetFullPath(fileName);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [".exe", ".cmd", ".bat"])
            : [""];

        var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        {
            try
            {
                var trimmed = dir.Trim('\"', ' ');
                if (!Directory.Exists(trimmed)) continue;

                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(trimmed, fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FindWellKnownPowerShell(string fileName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        if (fileName.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var standardPwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(standardPwsh)) return standardPwsh;
        }
        else if (fileName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
        {
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var winPowerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(winPowerShell)) return winPowerShell;
        }

        return null;
    }
}

internal sealed class ProcessPtySession : IPtySession
{
    private readonly Process process;
    private readonly CombinedPtyStream outputStream;
    private int disposed;

    public string SessionId { get; }
    public int ProcessId => process.Id;
    public bool HasExited => process.HasExited;
    public int? ExitCode => process.HasExited ? process.ExitCode : null;
    public bool? IsShellIdle => ProcessChildren.HasChildren(process.Id) is { } children ? !children : null;
    public Stream StandardInput => process.StandardInput.BaseStream;
    public Stream StandardOutput => outputStream;

    public ProcessPtySession(string sessionId, Process process)
    {
        SessionId = sessionId;
        this.process = process;
        outputStream = new CombinedPtyStream(process.StandardOutput.BaseStream, process.StandardError.BaseStream);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        await outputStream.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
    }
}

/// <summary>
/// Merges stdout and stderr into a single readable stream for xterm.js consumption.
/// </summary>
internal sealed class CombinedPtyStream : Stream
{
    private readonly Stream stdout;
    private readonly Stream stderr;
    private readonly Channel<byte[]> channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = true
    });
    private readonly CancellationTokenSource cts = new();
    private byte[]? pendingChunk;
    private int pendingOffset;
    private int disposed;

    public CombinedPtyStream(Stream stdout, Stream stderr)
    {
        this.stdout = stdout;
        this.stderr = stderr;

        var pumpStdout = PumpStreamAsync(stdout);
        var pumpStderr = PumpStreamAsync(stderr);

        _ = Task.WhenAll(pumpStdout, pumpStderr).ContinueWith(_ =>
        {
            channel.Writer.TryComplete();
        }, TaskScheduler.Default);
    }

    private async Task PumpStreamAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await channel.Writer.WriteAsync(chunk, cts.Token).ConfigureAwait(false);
            }
        }
        catch { }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        while (pendingChunk is null || pendingOffset >= pendingChunk.Length)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            try
            {
                if (await channel.Reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    if (channel.Reader.TryRead(out var chunk))
                    {
                        pendingChunk = chunk;
                        pendingOffset = 0;
                        break;
                    }
                }
                else
                {
                    return 0;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                return 0;
            }
        }

        var available = pendingChunk.Length - pendingOffset;
        var toCopy = Math.Min(buffer.Length, available);
        pendingChunk.AsMemory(pendingOffset, toCopy).CopyTo(buffer);
        pendingOffset += toCopy;
        if (pendingOffset >= pendingChunk.Length)
        {
            pendingChunk = null;
            pendingOffset = 0;
        }
        return toCopy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (disposing)
        {
            cts.Cancel();
            cts.Dispose();
            channel.Writer.TryComplete();
            stdout.Dispose();
            stderr.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cts.Cancel();
        cts.Dispose();
        channel.Writer.TryComplete();
        await stdout.DisposeAsync().ConfigureAwait(false);
        await stderr.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
