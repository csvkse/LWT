using System.Runtime.InteropServices;
namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters.Native;

/// <summary>
/// 封装由 Rust easytier_ffi 分配的 UTF-8 字符串裸指针，
/// 在释放时保证调用 free_string 归还给 Rust 分配器，防止原生内存泄漏。
/// </summary>
internal sealed class SafeEasyTierStringHandle : SafeHandle
{
    public SafeEasyTierStringHandle() : base(IntPtr.Zero, true)
    {
    }

    public SafeEasyTierStringHandle(IntPtr handle) : base(IntPtr.Zero, true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            EasyTierNativeMethods.free_string(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }

    public string ToUtf8StringAndDispose()
    {
        if (IsInvalid) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(handle) ?? string.Empty;
        }
        finally
        {
            Dispose();
        }
    }
}
