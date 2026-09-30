using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Terminal;
using Microsoft.Win32.SafeHandles;

namespace LinuxWebTool.Infrastructure.Terminal;

/// <summary>
/// Native Windows Pseudo Console (ConPTY) implementation using kernel32.dll APIs.
/// Supported on Windows 10 (1809+) and Windows 11.
/// </summary>
internal static class WindowsConPtyNative
{
    public const int S_OK = 0;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    public const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}

public sealed class WindowsConPtySession : IPtySession
{
    private readonly IntPtr hPC;
    private readonly IntPtr hProcess;
    private readonly SafeFileHandle inPipeWriteHandle;
    private readonly SafeFileHandle outPipeReadHandle;
    private readonly Stream standardInput;
    private readonly Stream standardOutput;
    private int disposed;

    public string SessionId { get; }
    public int ProcessId { get; }

    public bool HasExited
    {
        get
        {
            if (hProcess == IntPtr.Zero) return true;
            if (WindowsConPtyNative.GetExitCodeProcess(hProcess, out var exitCode))
            {
                return exitCode != WindowsConPtyNative.STILL_ACTIVE;
            }
            try
            {
                using var proc = Process.GetProcessById(ProcessId);
                return proc.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public Stream StandardInput => standardInput;
    public Stream StandardOutput => standardOutput;

    public WindowsConPtySession(
        string sessionId,
        int processId,
        IntPtr hProcess,
        IntPtr hPC,
        SafeFileHandle inPipeWriteHandle,
        SafeFileHandle outPipeReadHandle)
    {
        SessionId = sessionId;
        ProcessId = processId;
        this.hProcess = hProcess;
        this.hPC = hPC;
        this.inPipeWriteHandle = inPipeWriteHandle;
        this.outPipeReadHandle = outPipeReadHandle;

        standardInput = new FileStream(inPipeWriteHandle, FileAccess.Write, 4096, isAsync: false);
        standardOutput = new FileStream(outPipeReadHandle, FileAccess.Read, 4096, isAsync: false);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (hPC != IntPtr.Zero && !HasExited)
        {
            var size = new WindowsConPtyNative.COORD
            {
                X = (short)Math.Max(2, Math.Min(columns, 500)),
                Y = (short)Math.Max(1, Math.Min(rows, 500))
            };
            try
            {
                WindowsConPtyNative.ResizePseudoConsole(hPC, size);
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return Task.CompletedTask;

        try
        {
            using var proc = Process.GetProcessById(ProcessId);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { }

        CleanupNativeHandles();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        await standardInput.DisposeAsync().ConfigureAwait(false);
        await standardOutput.DisposeAsync().ConfigureAwait(false);
        inPipeWriteHandle.Dispose();
        outPipeReadHandle.Dispose();
    }

    private void CleanupNativeHandles()
    {
        if (hPC != IntPtr.Zero)
        {
            try { WindowsConPtyNative.ClosePseudoConsole(hPC); } catch { }
        }

        if (hProcess != IntPtr.Zero)
        {
            try { WindowsConPtyNative.CloseHandle(hProcess); } catch { }
        }
    }
}

internal static class WindowsConPtyFactory
{
    // ConPTY emits Win32 Input Mode (?9001h) sequences and attaches to host consoles in WebHost runners.
    // ProcessPtySession with CombinedPtyStream provides seamless, bidirectional UTF-8 piping across all platforms.
    public static bool IsSupported => false;

    public static IPtySession? TryCreateSession(
        string sessionId,
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        int columns,
        int rows,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (!IsSupported) return null;

        IntPtr hInRead = IntPtr.Zero;
        IntPtr hInWrite = IntPtr.Zero;
        IntPtr hOutRead = IntPtr.Zero;
        IntPtr hOutWrite = IntPtr.Zero;
        IntPtr hPC = IntPtr.Zero;
        IntPtr lpAttributeList = IntPtr.Zero;

        try
        {
            if (!WindowsConPtyNative.CreatePipe(out hInRead, out hInWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe for ConPTY.");

            if (!WindowsConPtyNative.CreatePipe(out hOutRead, out hOutWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe for ConPTY.");

            var size = new WindowsConPtyNative.COORD
            {
                X = (short)Math.Max(2, columns),
                Y = (short)Math.Max(1, rows)
            };

            var hr = WindowsConPtyNative.CreatePseudoConsole(size, hInRead, hOutWrite, 0, out hPC);
            if (hr != WindowsConPtyNative.S_OK || hPC == IntPtr.Zero)
                throw new Win32Exception(hr, "Failed to create PseudoConsole.");

            var lpSize = IntPtr.Zero;
            WindowsConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            lpAttributeList = Marshal.AllocHGlobal(lpSize);

            if (!WindowsConPtyNative.InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize proc thread attribute list.");

            if (!WindowsConPtyNative.UpdateProcThreadAttribute(
                lpAttributeList,
                0,
                (IntPtr)WindowsConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update proc thread attribute with ConPTY.");
            }

            var siex = new WindowsConPtyNative.STARTUPINFOEX();
            siex.StartupInfo.cb = Marshal.SizeOf<WindowsConPtyNative.STARTUPINFOEX>();
            siex.lpAttributeList = lpAttributeList;

            var cmdLine = BuildCommandLine(executable, arguments);

            var cwd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                ? workingDirectory
                : null;

            if (!WindowsConPtyNative.CreateProcess(
                null,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                WindowsConPtyNative.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                cwd,
                ref siex,
                out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to spawn process via ConPTY: {cmdLine}");
            }

            // Close child sides in parent process after child process has been successfully created
            WindowsConPtyNative.CloseHandle(hInRead);
            hInRead = IntPtr.Zero;
            WindowsConPtyNative.CloseHandle(hOutWrite);
            hOutWrite = IntPtr.Zero;

            if (pi.hThread != IntPtr.Zero)
            {
                WindowsConPtyNative.CloseHandle(pi.hThread);
            }

            var inSafeHandle = new SafeFileHandle(hInWrite, ownsHandle: true);
            var outSafeHandle = new SafeFileHandle(hOutRead, ownsHandle: true);

            return new WindowsConPtySession(sessionId, pi.dwProcessId, pi.hProcess, hPC, inSafeHandle, outSafeHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsConPty] Exception in TryCreateSession: {ex}");
            if (hInRead != IntPtr.Zero) WindowsConPtyNative.CloseHandle(hInRead);
            if (hInWrite != IntPtr.Zero) WindowsConPtyNative.CloseHandle(hInWrite);
            if (hOutRead != IntPtr.Zero) WindowsConPtyNative.CloseHandle(hOutRead);
            if (hOutWrite != IntPtr.Zero) WindowsConPtyNative.CloseHandle(hOutWrite);
            if (hPC != IntPtr.Zero) WindowsConPtyNative.ClosePseudoConsole(hPC);
            return null;
        }
        finally
        {
            if (lpAttributeList != IntPtr.Zero)
            {
                WindowsConPtyNative.DeleteProcThreadAttributeList(lpAttributeList);
                Marshal.FreeHGlobal(lpAttributeList);
            }
        }
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var exeQuoted = executable.Contains(' ') && !executable.StartsWith('\"') ? $"\"{executable}\"" : executable;
        if (arguments.Count == 0) return exeQuoted;

        var parts = new List<string> { exeQuoted };
        foreach (var arg in arguments)
        {
            if (string.IsNullOrEmpty(arg))
            {
                parts.Add("\"\"");
            }
            else if (arg.Contains(' ') && !arg.StartsWith('\"'))
            {
                parts.Add($"\"{arg}\"");
            }
            else
            {
                parts.Add(arg);
            }
        }
        return string.Join(" ", parts);
    }
}
