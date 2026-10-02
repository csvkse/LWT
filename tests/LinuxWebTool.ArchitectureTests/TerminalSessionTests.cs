using System.Text;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Terminal;
using LinuxWebTool.Infrastructure.Terminal;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class TerminalSessionTests
{
    [Fact]
    public async Task Windows_native_terminal_preserves_unicode_arguments_environment_and_exit_code()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await using var session = await new CrossPlatformPtyEngine().StartSessionAsync(new(
            Executable: executable,
            Arguments: ["-NoLogo", "-NoProfile", "-Command", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); Write-Output ('中文 ' + [char]34 + 'quoted' + [char]34 + ' ' + $env:LWT_NATIVE_TEST); exit 7"],
            EnvironmentVariables: new Dictionary<string, string> { ["LWT_NATIVE_TEST"] = "环境 space" }));
        Assert.IsType<WindowsConPtySession>(session);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(session.StandardOutput, Encoding.UTF8, leaveOpen: true);
        var output = "";
        var buffer = new char[4096];
        while (!output.Contains("中文 \"quoted\" 环境 space", StringComparison.Ordinal))
        {
            var count = await reader.ReadAsync(buffer, timeout.Token);
            Assert.True(count > 0, output);
            output += new string(buffer, 0, count);
        }
        await WaitUntilAsync(() => session.HasExited);
        Assert.Equal(7, session.ExitCode);
        await reader.ReadToEndAsync(timeout.Token);
    }

    [Fact]
    public async Task Default_shell_reports_directory_and_unused_session_is_cleaned()
    {
        if (!OperatingSystem.IsWindows() && !LinuxNativePty.IsSupported) return;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Terminal:UnusedGraceSeconds"] = "1" }).Build();
        await using var manager = new PtySessionManager(new CrossPlatformPtyEngine(), config);
        var session = await manager.CreateSessionAsync(new(WorkingDirectory: Path.GetTempPath()));
        await WaitUntilAsync(() => manager.GetSessionInfo(session.SessionId)?.WorkingDirectory is not null);
        Assert.False(manager.GetSessionInfo(session.SessionId)!.HasUserInput);
        Assert.True(session.IsShellIdle, $"The shell must be recognized as idle after its first prompt (value: {session.IsShellIdle}, children: {string.Join(",", ProcessChildren.GetChildren(session.ProcessId) ?? [])}).");
        await WaitUntilAsync(() => manager.GetSessionInfo(session.SessionId) is null);
    }

    [Fact]
    public async Task Windows_interactive_terminal_handles_input_resize_interrupt_and_reattach()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var manager = new PtySessionManager(new CrossPlatformPtyEngine());
        var session = await manager.CreateSessionAsync(new(WorkingDirectory: Path.GetTempPath()));
        Assert.IsType<WindowsConPtySession>(session);
        await WaitUntilAsync(() => manager.GetSessionInfo(session.SessionId)?.WorkingDirectory is not null);
        await using var attachment = manager.Attach(session.SessionId)!;
        var output = new StringBuilder();
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try { await foreach (var frame in attachment.ReadAllAsync(stop.Token)) lock (output) output.Append(frame.Data); }
            catch (OperationCanceledException) { }
        });
        bool Contains(string text) { lock (output) return output.ToString().Contains(text, StringComparison.Ordinal); }
        try
        {
            await attachment.WriteInputAsync(Encoding.UTF8.GetBytes("Write-Output ('INPUT:' + '中文 space')\r"));
            await WaitUntilAsync(() => Contains("INPUT:中文 space"));
            await attachment.ResizeAsync(110, 31);
            await attachment.WriteInputAsync(Encoding.UTF8.GetBytes("Write-Output ('SIZE:' + [Console]::WindowWidth + ':' + [Console]::WindowHeight)\r"));
            await WaitUntilAsync(() => Contains("SIZE:110:31"));
            await attachment.WriteInputAsync(Encoding.UTF8.GetBytes("Start-Sleep 30\r"));
            await Task.Delay(300);
            await attachment.WriteInputAsync(new byte[] { 3 });
            await WaitUntilAsync(() => manager.GetSessionInfo(session.SessionId)?.WorkingDirectory is not null);
            Assert.False(session.HasExited);
            await attachment.WriteInputAsync(Encoding.UTF8.GetBytes("Write-Output ('AFTER:' + 'INTERRUPT')\r"));
            await WaitUntilAsync(() => Contains("AFTER:INTERRUPT"));
        }
        catch (Exception ex) { lock (output) throw new InvalidOperationException(output.ToString(), ex); }
        finally { stop.Cancel(); await pump; }
        await attachment.DisposeAsync();
        await using var restored = manager.Attach(session.SessionId)!;
        Assert.Equal(session.ProcessId, manager.GetSessionInfo(session.SessionId)!.ProcessId);
    }

    [Fact]
    public async Task Output_continues_without_attachment_and_replay_is_bounded()
    {
        await using var pty = new TestPty();
        await using var manager = new PtySessionManager(new TestEngine(pty));
        await manager.CreateSessionAsync(new());
        for (var index = 0; index < 600; index++) pty.Output.Writer.TryWrite(Encoding.UTF8.GetBytes($"line-{index}\n"));
        await WaitUntilAsync(() => manager.GetSessionInfo(pty.SessionId)!.LastSequence >= 600);
        Assert.True(manager.GetSessionInfo(pty.SessionId)!.BufferTruncated);
        await using var attachment = manager.Attach(pty.SessionId)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = attachment.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.True(reader.Current.Truncated);
        Assert.Throws<InvalidOperationException>(() => manager.Attach(pty.SessionId));
        Assert.False(manager.GetSessionInfo(pty.SessionId)!.HasUserInput);
        await manager.WriteInputAsync(pty.SessionId, Encoding.UTF8.GetBytes("\x03"));
        Assert.True(manager.GetSessionInfo(pty.SessionId)!.HasUserInput);
    }

    [Fact]
    public async Task Unused_cleanup_requires_a_prompt_and_verified_idle_shell()
    {
        await using var pty = new TestPty();
        await using var session = new BackgroundPtySession(pty, Path.GetTempPath(), 4096);
        session.Start();
        var future = DateTimeOffset.UtcNow.AddMinutes(10);
        Assert.False(session.TryMarkForCleanup(future, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        pty.Output.Writer.TryWrite(Encoding.UTF8.GetBytes("\x1b]7;" + new Uri(Path.GetTempPath()).AbsoluteUri + "\x07\x1b]133;A\x07"));
        await WaitUntilAsync(() => session.Snapshot().WorkingDirectory is not null);
        var confirmedDirectory = session.Snapshot().WorkingDirectory;
        foreach (var reply in new[] { "\x1b[I", "\x1b[O", "\x1b[1;1R", "\x1b[?1;2c" })
            await session.WriteInputAsync(Encoding.UTF8.GetBytes(reply), CancellationToken.None);
        Assert.False(session.Snapshot().HasUserInput);
        Assert.Equal(confirmedDirectory, session.Snapshot().WorkingDirectory);
        pty.Idle = null;
        Assert.False(session.TryMarkForCleanup(future, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        pty.Idle = true;
        session.Update(new(KeepAlive: true));
        Assert.False(session.TryMarkForCleanup(future, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        session.Update(new(KeepAlive: false));
        Assert.True(session.TryMarkForCleanup(future, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Null(session.Attach(0));
    }

    [Fact]
    public async Task Tickets_are_single_use_and_scoped_to_a_session()
    {
        await using var pty = new TestPty();
        await using var manager = new PtySessionManager(new TestEngine(pty));
        await manager.CreateSessionAsync(new());
        var ticket = manager.IssueAttachmentTicket(pty.SessionId)!;
        Assert.True(manager.ConsumeAttachmentTicket(pty.SessionId, ticket));
        Assert.False(manager.ConsumeAttachmentTicket(pty.SessionId, ticket));
        Assert.False(manager.ConsumeAttachmentTicket("other", manager.IssueAttachmentTicket(pty.SessionId)!));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestEngine(TestPty session) : IPtyEngine
    {
        public bool IsSupported => true;
        public Task<IPtySession> StartSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IPtySession>(session);
    }
    private sealed class TestPty : IPtySession
    {
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public bool? Idle { get; set; } = true;
        public bool? IsShellIdle => Idle;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public int ProcessId => 1;
        public bool HasExited { get; private set; }
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput => outputStream ??= new OutputStream(Output.Reader);
        private Stream? outputStream;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TerminateAsync(CancellationToken cancellationToken = default) { HasExited = true; Output.Writer.TryComplete(); return Task.CompletedTask; }
        public async ValueTask DisposeAsync() { await TerminateAsync(); }
    }
    private sealed class OutputStream(ChannelReader<byte[]> reader) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await reader.WaitToReadAsync(cancellationToken)) return 0;
            var bytes = await reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
