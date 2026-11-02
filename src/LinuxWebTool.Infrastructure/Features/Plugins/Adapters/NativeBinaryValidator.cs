using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 原生程序与微服务可执行文件格式校验器。
/// 验证二进制文件头部魔数（ELF / PE / Shebang）以及 CPU 指令集架构，
/// 防止跨平台误用（如 Linux 环境执行 Windows PE 二进制导致 Exec format error）。
/// </summary>
public static class NativeBinaryValidator
{
    private const ushort ElfMachineAmd64 = 0x003E; // 62: AMD x86-64
    private const ushort ElfMachineArm64 = 0x00B7; // 183: ARM AArch64
    private const ushort ElfMachineArm32 = 0x0028; // 40: ARM 32-bit
    private const ushort ElfMachineX86 = 0x0003;   // 3: Intel 80386

    /// <summary>
    /// 校验指定路径的可执行文件是否符合当前操作系统的二进制或脚本格式要求。
    /// </summary>
    public static (bool IsValid, string? ErrorReason) ValidateExecutable(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return (false, "可执行文件路径为空");
        }

        if (!File.Exists(filePath))
        {
            return (false, $"可执行文件不存在: {filePath}");
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4)
            {
                return (false, $"文件过小 ({fs.Length} 字节)，不是有效的可执行文件");
            }

            Span<byte> header = stackalloc byte[20];
            var bytesRead = fs.Read(header);
            return ValidateHeader(header[..bytesRead]);
        }
        catch (Exception ex)
        {
            return (false, $"读取可执行文件头部失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 基于二进制头部字节进行平台与架构校验。
    /// </summary>
    public static (bool IsValid, string? ErrorReason) ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return (false, "头部数据不足 4 字节");
        }

        // 1. 脚本文件：以 '#!' (0x23, 0x21) 开头的 Shebang 脚本
        if (header[0] == 0x23 && header[1] == 0x21)
        {
            return (true, null);
        }

        // 2. Windows PE (MZ) 魔数
        var isPe = header[0] == 0x4D && header[1] == 0x5A;

        // 3. Linux/Unix ELF (\x7fELF) 魔数
        var isElf = header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46;

        if (OperatingSystem.IsWindows())
        {
            if (isElf)
            {
                return (false, "检测到 Linux ELF 二进制程序，当前运行于 Windows 宿主环境，无法直接执行");
            }

            if (!isPe)
            {
                return (false, $"未知二进制格式（Magic: 0x{header[0]:X2}{header[1]:X2}），Windows 环境需要 PE 可执行文件 (.exe)");
            }

            return (true, null);
        }
        else // Linux, macOS, FreeBSD etc.
        {
            if (isPe)
            {
                return (false, "检测到 Windows PE 可执行文件 (MZ)，当前运行于 Linux 系统环境，无法执行 (Exec format error)。请重新编译为 Linux ELF 二进制");
            }

            if (!isElf)
            {
                return (false, $"未知二进制格式（Magic: 0x{header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2}），Linux 环境仅支持 ELF 原生二进制或 #! 脚本");
            }

            // 架构校验 (e_machine at offset 18-19, little endian)
            if (header.Length >= 20)
            {
                var machine = (ushort)(header[18] | (header[19] << 8));
                var hostArch = RuntimeInformation.ProcessArchitecture;

                if (hostArch == Architecture.X64 && machine != ElfMachineAmd64)
                {
                    return (false, $"ELF 架构不匹配：当前宿主为 x86_64/AMD64，二进制目标架构为 0x{machine:X4}");
                }

                if (hostArch == Architecture.Arm64 && machine != ElfMachineArm64)
                {
                    return (false, $"ELF 架构不匹配：当前宿主为 AArch64/ARM64，二进制目标架构为 0x{machine:X4}");
                }
            }

            return (true, null);
        }
    }

    /// <summary>
    /// 检查指定文件是否为有效的 Linux ELF 文件。
    /// </summary>
    public static bool IsLinuxElf(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4) return false;
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4 && magic[0] == 0x7F && magic[1] == 0x45 && magic[2] == 0x4C && magic[3] == 0x46;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查指定文件是否为 Windows PE 可执行文件。
    /// </summary>
    public static bool IsWindowsPe(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 2) return false;
            Span<byte> magic = stackalloc byte[2];
            return fs.Read(magic) == 2 && magic[0] == 0x4D && magic[1] == 0x5A;
        }
        catch
        {
            return false;
        }
    }
}
