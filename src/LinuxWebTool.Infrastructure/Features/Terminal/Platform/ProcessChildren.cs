using System.Runtime.InteropServices;
namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

internal static class ProcessChildren
{
    public static bool? HasChildren(int pid)
        => GetChildren(pid) is { } children ? children.Any(name => !name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) : null;

    internal static IReadOnlyList<string>? GetChildren(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            var children = new List<string>();
            do { if (entry.ParentProcessId == pid) children.Add(entry.Executable ?? "unknown"); } while (Process32Next(snapshot, ref entry));
            return Marshal.GetLastWin32Error() == 18 ? children : null;
        }
        finally { WindowsConPtyNative.CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
}
