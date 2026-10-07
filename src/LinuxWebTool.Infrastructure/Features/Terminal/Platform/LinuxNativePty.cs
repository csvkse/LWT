using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

internal static class LinuxNativePty
{
    private const string Library = "linuxwebtool_pty";
    private static readonly Lazy<bool> available = new(() =>
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "liblinuxwebtool_pty.so"), out var handle)) return false;
        try { return new[] { "lwt_pty_start", "lwt_pty_read", "lwt_pty_write", "lwt_pty_resize", "lwt_pty_wait", "lwt_pty_close", "lwt_pty_kill", "lwt_pty_foreground" }.All(name => NativeLibrary.TryGetExport(handle, name, out _)); }
        finally { NativeLibrary.Free(handle); }
    });
    public static bool IsSupported => available.Value;

    public static IPtySession Start(string id, string executable, IReadOnlyList<string> args, PtyStartOptions options)
    {
        var path = Path.IsPathFullyQualified(executable) ? executable : (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")
            .Split(':').Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("终端程序不存在", executable);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        if (options.EnvironmentVariables is not null)
            foreach (var (key, value) in options.EnvironmentVariables) environment[key] = value;
        using var argv = new NativeStrings(new[] { path }.Concat(args));
        using var envp = new NativeStrings(environment.Select(entry => entry.Key + "=" + entry.Value));
        var pid = StartNative(path, argv.Pointer, envp.Pointer, options.WorkingDirectory,
            Math.Clamp(options.Columns, 1, 500), Math.Clamp(options.Rows, 1, 200), out var fd);
        if (pid < 0) throw new Win32Exception(-pid, "无法启动 Linux PTY");
        return new LinuxPtySession(id, pid, fd);
    }

    [DllImport(Library, EntryPoint = "lwt_pty_start")]
    private static extern int StartNative([MarshalAs(UnmanagedType.LPUTF8Str)] string executable, IntPtr argv, IntPtr envp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? cwd, int columns, int rows, out int master);
    [DllImport(Library, EntryPoint = "lwt_pty_read")] internal static extern int Read(int fd, [Out] byte[] buffer, int length);
    [DllImport(Library, EntryPoint = "lwt_pty_write")] internal static extern int Write(int fd, byte[] buffer, int length);
    [DllImport(Library, EntryPoint = "lwt_pty_resize")] internal static extern int Resize(int fd, int columns, int rows);
    [DllImport(Library, EntryPoint = "lwt_pty_foreground")] internal static extern int Foreground(int fd);
    [DllImport(Library, EntryPoint = "lwt_pty_wait")] internal static extern int Wait(int pid, out int exitCode);
    [DllImport(Library, EntryPoint = "lwt_pty_close")] internal static extern void Close(int fd);
    [DllImport(Library, EntryPoint = "lwt_pty_kill")] internal static extern void Kill(int pid);

    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] values;
        public IntPtr Pointer { get; }
        public NativeStrings(IEnumerable<string> strings)
        {
            values = strings.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((values.Length + 1) * IntPtr.Size);
            for (var i = 0; i < values.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, values[i]);
            Marshal.WriteIntPtr(Pointer, values.Length * IntPtr.Size, IntPtr.Zero);
        }
        public void Dispose()
        {
            foreach (var value in values) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }
}

internal sealed class LinuxPtySession(string id, int pid, int fd) : IPtySession
{
    private readonly object gate = new();
    private readonly Stream stream = new LinuxPtyStream(fd);
    private int? exitCode;
    private bool closed;
    public string SessionId => id;
    public int ProcessId => pid;
    public bool IsNativePty => true;
    public Stream StandardInput => stream;
    public Stream StandardOutput => stream;
    public bool HasExited
    {
        get
        {
            lock (gate)
            {
                if (exitCode.HasValue) return true;
                if (LinuxNativePty.Wait(pid, out var code) == 1) exitCode = code;
                return exitCode.HasValue;
            }
        }
    }
    public int? ExitCode { get { _ = HasExited; return exitCode; } }
    public bool? IsShellIdle
    {
        get
        {
            lock (gate)
            {
                if (closed || HasExited) return false;
                try
                {
                    if (LinuxNativePty.Foreground(fd) != pid) return false;
                    if (!string.IsNullOrWhiteSpace(File.ReadAllText($"/proc/{pid}/task/{pid}/children"))) return false;
                    var stat = File.ReadAllText($"/proc/{pid}/stat");
                    return stat[(stat.LastIndexOf(')') + 2)..].StartsWith("S ", StringComparison.Ordinal);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }
    }
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var result = LinuxNativePty.Resize(fd, columns, rows);
            if (result < 0) throw new Win32Exception(-result);
        }
        return Task.CompletedTask;
    }
    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        // Interactive jobs have their own process groups. Kill the process tree and remaining session members.
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var member) || member == pid) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields.Length > 3 && fields[3] == pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    using var process = System.Diagnostics.Process.GetProcessById(member);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        lock (gate) { if (!HasExited) LinuxNativePty.Kill(pid); }
        for (var attempt = 0; attempt < 100 && !HasExited; attempt++)
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; }
        await TerminateAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class LinuxPtyStream(int descriptor) : Stream
    {
        private readonly object ioGate = new();
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanWrite => !disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            var bytes = new byte[Math.Min(4096, buffer.Length)];
            var count = await Task.Run(() =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int result;
                    lock (ioGate)
                    {
                        if (disposed) return 0;
                        result = LinuxNativePty.Read(descriptor, bytes, bytes.Length);
                    }
                    if (result == -11 || result == -4) { Thread.Sleep(1); continue; }
                    if (result < 0) throw new IOException("PTY 读取失败", new Win32Exception(-result));
                    return result;
                }
            }, cancellationToken).ConfigureAwait(false);
            bytes.AsMemory(0, count).CopyTo(buffer);
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = buffer[..Math.Min(buffer.Length, 4096)].ToArray();
                int result;
                lock (ioGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    result = LinuxNativePty.Write(descriptor, bytes, bytes.Length);
                }
                if (result == -11 || result == -4) { await Task.Delay(10, cancellationToken).ConfigureAwait(false); continue; }
                if (result <= 0) throw new IOException("PTY 写入失败", new Win32Exception(-result));
                buffer = buffer[result..];
            }
        }
        protected override void Dispose(bool disposing)
        {
            lock (ioGate) { if (!disposed) { disposed = true; LinuxNativePty.Close(descriptor); } }
            base.Dispose(disposing);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
