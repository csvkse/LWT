using System.Text;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Terminal;

namespace LinuxWebTool.Infrastructure.Terminal;

internal sealed class BackgroundPtySession(IPtySession pty, string? cwd, int limit) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Queue<TerminalOutputFrame> history = new();
    private readonly TerminalDirectoryReportParser directoryParser = new();
    private string? reportedDirectory;
    private string? pendingReportedDirectory;
    private bool atPrompt;
    private readonly DateTimeOffset created = DateTimeOffset.UtcNow;
    private readonly int processId = pty.ProcessId;
    private int? exitCode;
    private Task pump = Task.CompletedTask;
    private Attachment? attachment;
    private string name = "终端 " + pty.SessionId[..Math.Min(8, pty.SessionId.Length)];
    private int buffered;
    private long sequence;
    private bool truncated;
    private bool hasInput;
    private bool keep;
    private bool closing;
    private bool ended;
    private DateTimeOffset? lastInput;
    private DateTimeOffset? detached = DateTimeOffset.UtcNow;
    private DateTimeOffset? exited;
    public IPtySession Pty => pty;

    public void Start() => pump = PumpAsync();

    public TerminalSessionInfo Snapshot()
    {
        lock (gate)
        {
            if (atPrompt && pendingReportedDirectory is not null && pty.IsShellIdle == true)
            {
                reportedDirectory = pendingReportedDirectory;
                pendingReportedDirectory = null;
            }
            var hasExited = closing || ended || pty.HasExited;
            if (!closing && pty.HasExited) exitCode = pty.ExitCode;
            return new(pty.SessionId, processId, name, hasExited ? "Exited" : attachment is null ? "RunningDetached" : "RunningAttached",
                cwd, reportedDirectory, created, lastInput, detached, hasInput, keep, exitCode, truncated, sequence, pty.IsNativePty);
        }
    }

    public bool Update(TerminalSessionUpdateRequest request)
    {
        lock (gate)
        {
            if (closing) return false;
            if (request.Name is not null) name = request.Name;
            if (request.KeepAlive.HasValue) keep = request.KeepAlive.Value;
            return true;
        }
    }

    public IPtyAttachment? Attach(long after)
    {
        lock (gate)
        {
            if (closing || ended || pty.HasExited) return null;
            if (attachment is not null) throw new InvalidOperationException("会话已连接，请先在原窗口断开");
            attachment = new Attachment(this);
            detached = null;
            if (history.TryPeek(out var oldest) && after < oldest.Sequence - 1)
                attachment.Channel.Writer.TryWrite(new TerminalOutputFrame(oldest.Sequence - 1, "", true));
            foreach (var frame in history)
                if (frame.Sequence > after) attachment.Channel.Writer.TryWrite(frame);
            return attachment;
        }
    }

    public async Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken token)
    {
        if (input.IsEmpty) return;
        var userInput = !TerminalInputClassifier.IsProtocolReply(input.Span);
        await inputGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (closing || ended) throw new InvalidOperationException("会话已结束");
                if (userInput)
                {
                    atPrompt = false;
                    reportedDirectory = null;
                    pendingReportedDirectory = null;
                    directoryParser.ResetPrompt();
                }
            }
            await pty.StandardInput.WriteAsync(input, token).ConfigureAwait(false);
            await pty.StandardInput.FlushAsync(token).ConfigureAwait(false);
            if (userInput) lock (gate) { hasInput = true; lastInput = DateTimeOffset.UtcNow; }
        }
        finally { inputGate.Release(); }
    }

    private async Task PumpAsync()
    {
        var bytes = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var read = await pty.StandardOutput.ReadAsync(bytes, stop.Token).ConfigureAwait(false);
                var count = decoder.GetChars(bytes, 0, read, chars, 0, read == 0);
                if (count > 0) Publish(new string(chars, 0, count));
                if (read == 0) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (gate)
            {
                ended = true;
                exited = DateTimeOffset.UtcNow;
                attachment?.Channel.Writer.TryComplete();
            }
        }
    }

    private void Publish(string text)
    {
        lock (gate)
        {
            var directory = directoryParser.Feed(text);
            atPrompt = directoryParser.IsAtPrompt;
            if (directory is not null && atPrompt) pendingReportedDirectory = directory;
            var frame = new TerminalOutputFrame(++sequence, text);
            history.Enqueue(frame);
            buffered += Encoding.UTF8.GetByteCount(text);
            while (history.Count > 256 || buffered > limit)
            {
                buffered -= Encoding.UTF8.GetByteCount(history.Dequeue().Data);
                truncated = true;
            }
            if (attachment is not null && !attachment.Channel.Writer.TryWrite(frame))
            {
                attachment.Revoke();
                attachment = null;
                detached = DateTimeOffset.UtcNow;
            }
        }
    }

    public bool TryMarkForCleanup(DateTimeOffset now, TimeSpan unused, TimeSpan retention)
    {
        lock (gate)
        {
            if (closing || inputGate.CurrentCount == 0) return false;
            if (ended && exited.HasValue && now - exited.Value >= retention
                || !ended && attachment is null && !hasInput && !keep && detached.HasValue
                && now - detached.Value >= unused && atPrompt && pty.IsShellIdle == true)
            {
                closing = true;
                return true;
            }
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { closing = true; attachment?.Revoke(); }
        try { await pty.TerminateAsync().ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            try { await pty.DisposeAsync().ConfigureAwait(false); }
            finally { await pump.ConfigureAwait(false); stop.Dispose(); }
        }
    }

    private sealed class Attachment(BackgroundPtySession owner) : IPtyAttachment
    {
        private readonly CancellationTokenSource revoked = new();
        public Channel<TerminalOutputFrame> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<TerminalOutputFrame>(512);
        public async IAsyncEnumerable<TerminalOutputFrame> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked.Token);
            await foreach (var frame in Channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false)) yield return frame;
        }
        public void Revoke() { Channel.Writer.TryComplete(); revoked.Cancel(); }
        public Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            lock (owner.gate)
            {
                if (owner.attachment != this || revoked.IsCancellationRequested) throw new IOException("终端连接已失效");
                return owner.WriteInputAsync(input, cancellationToken);
            }
        }
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            lock (owner.gate)
            {
                if (owner.attachment != this || revoked.IsCancellationRequested) throw new IOException("终端连接已失效");
                return owner.Pty.ResizeAsync(columns, rows, cancellationToken);
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (owner.gate)
            {
                if (owner.attachment == this) { owner.attachment = null; owner.detached = DateTimeOffset.UtcNow; }
                Revoke();
            }
            return ValueTask.CompletedTask;
        }
    }
}
