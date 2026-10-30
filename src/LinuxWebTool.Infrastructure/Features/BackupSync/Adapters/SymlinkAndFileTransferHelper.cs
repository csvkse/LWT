using System.Text;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 跨平台文件复制、符号链接保真与并发文件状态安全操作辅助类。
/// 彻底隔离悬空符号链接（Broken Symlink）的 Dereference 异常，并提供单文件异常弹性防御。
/// </summary>
internal static class SymlinkAndFileTransferHelper
{
    /// <summary>
    /// 判断文件系统项是否为符号链接（重解析点）
    /// </summary>
    public static bool IsSymbolicLink(FileSystemInfo info)
    {
        if (info.LinkTarget is not null) return true;
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 安全获取符号链接的目标路径字符串（绝不 Dereference 目标真实内容，即使目标不存在也能安全返回）
    /// </summary>
    public static string? GetLinkTarget(FileSystemInfo info)
    {
        if (info.LinkTarget is not null) return info.LinkTarget;
        try
        {
            var fi = new FileInfo(info.FullName);
            if (fi.LinkTarget is not null) return fi.LinkTarget;
            var target = File.ResolveLinkTarget(info.FullName, returnFinalTarget: false);
            return target?.FullName;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 安全获取文件大小：普通文件返回实际大小；符号链接返回链接目标文本的字节数；若文件在读取时消失则返回 false
    /// </summary>
    public static bool TryGetSafeLength(FileInfo file, out long length)
    {
        length = 0;
        try
        {
            if (IsSymbolicLink(file))
            {
                var target = GetLinkTarget(file);
                length = string.IsNullOrEmpty(target) ? 0 : Encoding.UTF8.GetByteCount(target);
                return true;
            }

            length = file.Length;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 安全删除可能已存在的目标文件或残留符号链接（即使为损坏的悬空软链也能彻底清除）
    /// </summary>
    public static void SafeDeleteFileOrLink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (UnauthorizedAccessException)
        {
            try
            {
                Directory.Delete(path, recursive: false);
            }
            catch { }
        }
        catch (IOException)
        {
            try
            {
                Directory.Delete(path, recursive: false);
            }
            catch { }
        }
    }

    /// <summary>
    /// 在目标位置安全创建或重构相同的符号链接
    /// </summary>
    public static bool CreateOrUpdateSymlink(string targetFilePath, string linkTarget, ILogger? logger = null)
    {
        try
        {
            SafeDeleteFileOrLink(targetFilePath);
            File.CreateSymbolicLink(targetFilePath, linkTarget);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "创建符号链接失败：Target={Target}, LinkTarget={LinkTarget}", targetFilePath, linkTarget);
            return false;
        }
    }

    /// <summary>
    /// 确保目标目录已安全创建
    /// </summary>
    public static void EnsureDirectoryCreated(string targetFilePath)
    {
        var dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
