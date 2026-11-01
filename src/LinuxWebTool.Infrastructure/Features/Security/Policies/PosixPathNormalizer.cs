namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

/// <summary>
/// 纯 POSIX 路径词法规范化与边界匹配器（完全独立于宿主机操作系统，抗 ../ 穿越与前缀偷渡）
/// 专门用于远程受控 Linux 主机 SFTP 路径与跨平台规则判定
/// </summary>
public static class PosixPathNormalizer
{
    /// <summary>
    /// 纯 POSIX 规范化：消除相对路径 . 和 ..、合并多重斜杠、确保绝对路径根前缀
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Trim() == "~" || input.Trim() == "~/")
        {
            return "/";
        }

        var trimmed = input.Trim().Replace('\\', '/');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);

        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else
            {
                stack.Add(p);
            }
        }

        return "/" + string.Join('/', stack);
    }

    /// <summary>
    /// 严格按目录分量比对目标路径是否位于根路径内（抗 /home2 偷渡 /home）
    /// </summary>
    public static bool IsWithin(string targetPath, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot)) return false;
        if (allowedRoot.Trim() == "*") return true;

        var normTarget = Normalize(targetPath);
        var normRoot = Normalize(allowedRoot);

        if (normRoot == "/") return true;
        if (normTarget.Equals(normRoot, StringComparison.Ordinal)) return true;

        var rootWithSlash = normRoot.EndsWith('/') ? normRoot : normRoot + "/";
        return normTarget.StartsWith(rootWithSlash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 解析允许的路径列表（支持分号、逗号或换行切分）
    /// </summary>
    public static IReadOnlyList<string> ParseAllowedRoots(string? config)
    {
        if (string.IsNullOrWhiteSpace(config) || config.Trim() == "*")
        {
            return ["*"];
        }

        var tokens = config.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (!string.IsNullOrWhiteSpace(t))
            {
                list.Add(Normalize(t));
            }
        }
        return list;
    }
}
