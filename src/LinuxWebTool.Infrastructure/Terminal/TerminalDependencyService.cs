using System.Diagnostics;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Terminal;

namespace LinuxWebTool.Infrastructure.Terminal;

public sealed record TerminalCapabilities(
    [property: System.Text.Json.Serialization.JsonPropertyName("platform")] string Platform,
    [property: System.Text.Json.Serialization.JsonPropertyName("mode")] string Mode,
    [property: System.Text.Json.Serialization.JsonPropertyName("nativePty")] bool NativePty,
    [property: System.Text.Json.Serialization.JsonPropertyName("container")] bool Container,
    [property: System.Text.Json.Serialization.JsonPropertyName("tmuxInstalled")] bool TmuxInstalled,
    [property: System.Text.Json.Serialization.JsonPropertyName("canInstall")] bool CanInstall,
    [property: System.Text.Json.Serialization.JsonPropertyName("installCommand")] string? InstallCommand,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

public sealed record TerminalInstallStatus(
    [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State,
    [property: System.Text.Json.Serialization.JsonPropertyName("log")] string Log);

public sealed class TerminalDependencyService(IPtyEngine engine) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task job = Task.CompletedTask;
    private string state = "Idle";
    private string log = "";

    public TerminalCapabilities Detect()
    {
        var linux = OperatingSystem.IsLinux();
        var container = linux && (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv")
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true");
        var native = linux ? LinuxNativePty.IsSupported : OperatingSystem.IsWindows() && WindowsConPtyFactory.IsSupported;
        var installed = linux && FindProgram("tmux") is not null;
        var commands = linux ? ResolveInstallCommands() : null;
        var canInstall = linux && !container && !installed && commands is not null && commands.All(c => FindProgram(c.Program) is not null) && GetEffectiveUserId() == 0;
        var command = commands is null ? null : string.Join(" && ", commands.Select(c => c.Program + " " + string.Join(" ", c.Arguments)));
        var message = !native ? "当前使用管道回退，窗口缩放和全屏交互能力受限。后台会话仍可在浏览器断线后保留。"
            : "支持原生 PTY 和浏览器断线续接。";
        message += " 服务或容器重启会结束当前会话；tmux 安装成功也不会自动启用跨重启恢复。";
        if (container) message += " 容器依赖在镜像构建时安装。";
        else if (linux && !installed && !canInstall) message += " 一键安装需要支持的发行版与系统安装权限；可由管理员手动执行所示命令。";
        return new(linux ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "Unsupported", "Application", native, container, installed, canInstall, command, message);
    }

    public TerminalInstallStatus Status() { lock (gate) return new(state, log); }

    public bool StartInstall()
    {
        lock (gate)
        {
            if (state == "Running" || !Detect().CanInstall) return false;
            state = "Running";
            log = "开始安装 tmux。\n";
            job = Task.Run(InstallAsync);
            return true;
        }
    }

    private async Task InstallAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var commands = ResolveInstallCommands() ?? throw new InvalidOperationException("不支持当前发行版");
            foreach (var command in commands) await RunAsync(command.Program, command.Arguments, timeout.Token).ConfigureAwait(false);
            await RunAsync("tmux", ["-V"], timeout.Token).ConfigureAwait(false);
            await ProbeTmuxAsync(timeout.Token).ConfigureAwait(false);
            lock (gate) { state = "Succeeded"; Append("安装完成，临时会话的分离与重新附着验证通过。\n"); }
        }
        catch (Exception ex)
        {
            lock (gate) { state = "Failed"; Append(ex is OperationCanceledException ? "安装超时或服务停止；请检查包管理器状态。\n" : ex.Message + "\n"); }
        }
    }

    private async Task ProbeTmuxAsync(CancellationToken token)
    {
        if (!LinuxNativePty.IsSupported) throw new InvalidOperationException("tmux 已安装，但原生 PTY 不可用，无法验证附着能力");
        var socket = "lwt-probe-" + Guid.NewGuid().ToString("N");
        try
        {
            await RunAsync("tmux", ["-L", socket, "-f", "/dev/null", "new-session", "-d", "-s", "probe", "sleep 60"], token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var session = await engine.StartSessionAsync(new(Executable: FindProgram("tmux"), Arguments: ["-L", socket, "attach-session", "-t", "probe"]), token).ConfigureAwait(false);
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var buffer = new byte[4096];
                if (await session.StandardOutput.ReadAsync(buffer, probeTimeout.Token).ConfigureAwait(false) == 0) throw new IOException("tmux 附着失败");
                await session.StandardInput.WriteAsync(new byte[] { 2, (byte)'d' }, probeTimeout.Token).ConfigureAwait(false);
                while (!session.HasExited) await Task.Delay(50, probeTimeout.Token).ConfigureAwait(false);
                if (session.ExitCode != 0) throw new IOException("tmux 分离失败");
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await RunAsync("tmux", ["-L", socket, "kill-server"], cleanup.Token).ConfigureAwait(false); } catch { }
        }
    }

    private async Task RunAsync(string program, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(FindProgram(program) ?? throw new FileNotFoundException("缺少程序：" + program))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        using var process = Process.Start(start) ?? throw new IOException("无法启动安装程序");
        var output = DrainAsync(process.StandardOutput, token);
        var error = DrainAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"{program} 执行失败，退出码 {process.ExitCode}；请检查软件源、网络和包管理器锁");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            lock (gate) Append(new string(buffer, 0, count));
    }
    private void Append(string text) { log += text; if (log.Length > 16384) log = "[较早日志已截断]\n" + log[^16000..]; }

    internal sealed record InstallCommand(string Program, string[] Arguments);
    internal static InstallCommand[]? InstallCommands(string distribution) => distribution switch
    {
        "debian" or "ubuntu" => [new("apt-get", ["update"]), new("apt-get", ["install", "-y", "--no-install-recommends", "tmux"])],
        "fedora" or "rhel" or "centos" or "rocky" or "almalinux" => [new("dnf", ["install", "-y", "tmux"])],
        "arch" or "manjaro" => [new("pacman", ["-S", "--noconfirm", "tmux"])],
        "opensuse" or "opensuse-leap" or "opensuse-tumbleweed" => [new("zypper", ["--non-interactive", "install", "tmux"])],
        "alpine" => [new("apk", ["add", "--no-cache", "tmux"])],
        _ => null
    };
    private static string ReadDistribution()
    {
        try { return File.ReadLines("/etc/os-release").FirstOrDefault(line => line.StartsWith("ID=", StringComparison.Ordinal))?[3..].Trim('"', '\'').ToLowerInvariant() ?? ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
    private static InstallCommand[]? ResolveInstallCommands()
    {
        var commands = InstallCommands(ReadDistribution());
        if (commands is [{ Program: "dnf" }] && FindProgram("dnf") is null && FindProgram("yum") is not null)
            return [new("yum", ["install", "-y", "tmux"])];
        return commands;
    }
    private static string? FindProgram(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
    public async ValueTask DisposeAsync() { stop.Cancel(); await job.ConfigureAwait(false); stop.Dispose(); }
}
