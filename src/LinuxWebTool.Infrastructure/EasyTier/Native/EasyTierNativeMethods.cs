using System.Runtime.InteropServices;

namespace LinuxWebTool.Infrastructure.EasyTier.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct KeyValuePairNative
{
    public IntPtr Key;
    public IntPtr Value;
}

/// <summary>
/// EasyTier C ABI / FFI 现代源码生成互操作层（.NET 10 Native AOT 零反射兼容）
/// </summary>
internal static partial class EasyTierNativeMethods
{
    public const string LibraryName = "easytier_ffi";
    private static int _resolverRegistered;
    private static string? _customLibraryPath;

    static EasyTierNativeMethods()
    {
        EnsureResolverRegistered();
    }

    public static void SetCustomLibraryPath(string path)
    {
        _customLibraryPath = path;
    }

    public static void EnsureResolverRegistered()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(EasyTierNativeMethods).Assembly, ResolveDllImport);
        }
    }

    private static IntPtr ResolveDllImport(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        // 1. 若显式指定了路径
        if (!string.IsNullOrWhiteSpace(_customLibraryPath) && File.Exists(_customLibraryPath))
        {
            if (NativeLibrary.TryLoad(_customLibraryPath, out var handle))
            {
                return handle;
            }
        }

        // 2. 探测数据目录 data/easytier/bin/
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var libFileName = isWindows ? "easytier_ffi.dll" : "libeasytier_ffi.so";
        var probePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "easytier", "bin", libFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "easytier", "bin", libFileName),
            Path.Combine(AppContext.BaseDirectory, libFileName),
            Path.Combine(Directory.GetCurrentDirectory(), libFileName),
        };

        foreach (var path in probePaths)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        // 3. 兜底尝试默认路径
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var defaultHandle))
        {
            return defaultHandle;
        }

        return IntPtr.Zero;
    }

    public static bool ProbeLibrary(out string? resolvedPath)
    {
        EnsureResolverRegistered();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var libFileName = isWindows ? "easytier_ffi.dll" : "libeasytier_ffi.so";
        var probePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(_customLibraryPath)) probePaths.Add(_customLibraryPath);
        probePaths.Add(Path.Combine(AppContext.BaseDirectory, "data", "easytier", "bin", libFileName));
        probePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "data", "easytier", "bin", libFileName));
        probePaths.Add(Path.Combine(AppContext.BaseDirectory, libFileName));
        probePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), libFileName));

        foreach (var path in probePaths)
        {
            if (File.Exists(path))
            {
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    NativeLibrary.Free(handle);
                    resolvedPath = Path.GetFullPath(path);
                    return true;
                }
            }
        }

        if (NativeLibrary.TryLoad(LibraryName, typeof(EasyTierNativeMethods).Assembly, null, out var defaultHandle))
        {
            NativeLibrary.Free(defaultHandle);
            resolvedPath = LibraryName;
            return true;
        }

        resolvedPath = null;
        return false;
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int parse_config(string cfgStr);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int run_network_instance(string cfgStr);

    [LibraryImport(LibraryName)]
    public static partial int retain_network_instance(IntPtr instNames, nuint length);

    [LibraryImport(LibraryName)]
    public static partial int delete_network_instance(IntPtr instNames, nuint length);

    [LibraryImport(LibraryName)]
    public static partial int list_instance(IntPtr infos, nuint maxLength);

    [LibraryImport(LibraryName)]
    public static partial int collect_network_infos(IntPtr infos, nuint maxLength);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int set_tun_fd(string instName, int fd);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int call_json_rpc(
        string serviceName,
        string methodName,
        string? domainName,
        string payloadJson,
        out IntPtr outResponseJson);

    [LibraryImport(LibraryName)]
    public static partial void get_error_msg(out IntPtr errorMsg);

    [LibraryImport(LibraryName)]
    public static partial void free_string(IntPtr s);

    public static string GetLastErrorMessage()
    {
        get_error_msg(out var ptr);
        if (ptr == IntPtr.Zero) return "未知 EasyTier 内部错误";
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "未知 EasyTier 内部错误";
        }
        finally
        {
            free_string(ptr);
        }
    }
}
