using System.Diagnostics;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// Node.js 运行时适配器。
/// </summary>
public sealed class NodeRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Node;
    public string PrimaryExecutable => "node";
    public IReadOnlyList<string> CandidateExecutables => ["node"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        if (IsExecutableInPath("node"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        missingReason = "系统中未检测到 Node.js 运行时，请先安装 node (如: sudo apt install nodejs 或 nvm)。";
        missingRunners = ["node"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install OpenJS.NodeJS.LTS",
        "alpine" => "apk add --no-cache nodejs",
        "rhel" => "dnf install -y nodejs",
        _ => "apt update && apt install -y nodejs"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var entry = Path.Combine(pluginDir, manifest.Entrypoint);
        return new PluginStartSpec
        {
            FileName = "node",
            Arguments = $"\"{entry}\"",
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }

    internal static bool IsExecutableInPath(string exe)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static Dictionary<string, string> BuildStandardEnv(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv)
    {
        // 跨语言通用持久化架构：物理隔离数据卷与代码目录 (Code vs Volume Separation)
        string volumeDir;
        string runDir;
        string pluginsParent = Path.GetDirectoryName(pluginDir) ?? "";
        if (Path.GetFileName(pluginsParent).Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            var dataRoot = Path.GetDirectoryName(pluginsParent) ?? pluginsParent;
            volumeDir = Path.Combine(dataRoot, "plugins_data", "volumes", manifest.Id);
            runDir = Path.Combine(dataRoot, "plugins_data", "run", manifest.Id);
        }
        else
        {
            volumeDir = pluginDir;
            runDir = Path.Combine(pluginDir, "run");
        }

        var stateDir = Path.Combine(volumeDir, "state");
        try
        {
            Directory.CreateDirectory(volumeDir);
            Directory.CreateDirectory(stateDir);
            Directory.CreateDirectory(runDir);
        }
        catch { }

        // 历史平滑自愈迁移：若旧版路径下有数据文件，自动自愈同步至持久卷
        MigrateLegacyPluginData(manifest.Id, pluginDir, volumeDir, stateDir);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LWT_PLUGIN_ID"] = manifest.Id,
            ["LWT_CODE_DIR"] = pluginDir,
            ["LWT_VOLUME_DIR"] = volumeDir,
            ["LWT_STATE_DIR"] = stateDir,
            ["LWT_RUN_DIR"] = runDir,
            // 向下兼容历史 LWT_DATA_DIR
            ["LWT_DATA_DIR"] = volumeDir,
            ["LWT_SQLITE_PATH"] = Path.Combine(volumeDir, "data.db"),
            ["LWT_KV_PATH"] = Path.Combine(volumeDir, "kv.db"),
            ["HOST"] = "127.0.0.1",
            ["BIND_IP"] = "127.0.0.1",
            ["BIND_ADDRESS"] = "127.0.0.1"
        };

        if (port.HasValue)
        {
            var portKey = string.IsNullOrWhiteSpace(manifest.Network?.PortEnv) ? "PORT" : manifest.Network.PortEnv;
            dict[portKey] = port.Value.ToString();
            dict["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.Value}";
        }

        if (manifest.Environment != null)
        {
            foreach (var (k, v) in manifest.Environment) dict[k] = v;
        }

        if (extraEnv != null)
        {
            foreach (var (k, v) in extraEnv) dict[k] = v;
        }

        return dict;
    }

    private static void MigrateLegacyPluginData(string pluginId, string pluginDir, string volumeDir, string stateDir)
    {
        try
        {
            // 0. 迁移过渡路径 /data/volumes/{pluginId}
            var pluginsParent = Path.GetDirectoryName(pluginDir) ?? "";
            if (Path.GetFileName(pluginsParent).Equals("plugins", StringComparison.OrdinalIgnoreCase))
            {
                var dataRoot = Path.GetDirectoryName(pluginsParent) ?? pluginsParent;
                var oldVolumeDir = Path.Combine(dataRoot, "volumes", pluginId);
                if (Directory.Exists(oldVolumeDir) && !string.Equals(oldVolumeDir, volumeDir, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.GetFiles(oldVolumeDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(oldVolumeDir, file);
                        var dest = Path.Combine(volumeDir, rel);
                        if (!File.Exists(dest))
                        {
                            var destDir = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                            File.Copy(file, dest, overwrite: false);
                        }
                    }
                }
            }

            // 1. 迁移旧版 pluginDir/data 下的全部持久化文件
            var legacyDataDir = Path.Combine(pluginDir, "data");
            if (Directory.Exists(legacyDataDir) && !string.Equals(legacyDataDir, stateDir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.GetFiles(legacyDataDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(legacyDataDir, file);
                    var dest = Path.Combine(stateDir, rel);
                    if (!File.Exists(dest))
                    {
                        var destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                        File.Copy(file, dest, overwrite: false);
                    }
                }
            }

            // 2. 迁移旧版 pluginDir 根目录下的 kv.db 和 data.db
            foreach (var dbName in new[] { "kv.db", "data.db" })
            {
                var legacyDb = Path.Combine(pluginDir, dbName);
                var destDb = Path.Combine(volumeDir, dbName);
                if (File.Exists(legacyDb) && !File.Exists(destDb))
                {
                    File.Copy(legacyDb, destDb, overwrite: false);
                }
            }
        }
        catch
        {
            // 自愈迁移非致命，静默跳过保护宿主主流程
        }
    }
}

/// <summary>
/// Python 运行时适配器。
/// </summary>
public sealed class PythonRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Python;
    public string PrimaryExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
    public IReadOnlyList<string> CandidateExecutables => ["python3", "python"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        if (TestCommand(exe, "--version"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TestCommand("py", "--version"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        missingReason = "系统中未检测到 Python 3 运行时，请先安装 python3 (如: sudo apt install python3)。";
        missingRunners = [exe];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install Python.Python.3.12",
        "alpine" => "apk add --no-cache python3",
        "rhel" => "dnf install -y python3",
        _ => "apt update && apt install -y python3"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        env["PYTHONUNBUFFERED"] = "1";

        string exe = DetectPythonExecutable(pluginDir);
        var entry = Path.Combine(pluginDir, manifest.Entrypoint);

        return new PluginStartSpec
        {
            FileName = exe,
            Arguments = $"-u \"{entry}\"",
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }

    private static string DetectPythonExecutable(string pluginDir)
    {
        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var venvPy = isWin
            ? Path.Combine(pluginDir, ".venv", "Scripts", "python.exe")
            : Path.Combine(pluginDir, ".venv", "bin", "python3");

        if (File.Exists(venvPy)) return venvPy;
        return isWin ? "python" : "python3";
    }

    internal static bool TestCommand(string exe, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// .NET Core C# 运行时适配器。
/// </summary>
public sealed class DotNetRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.DotNet;
    public string PrimaryExecutable => FindDotNetExecutable() ?? "dotnet";
    public IReadOnlyList<string> CandidateExecutables => ["dotnet", "/usr/share/dotnet/dotnet"];

    public static string? FindDotNetExecutable()
    {
        if (PythonRuntimeAdapter.TestCommand("dotnet", "--version")) return "dotnet";
        if (File.Exists("/usr/share/dotnet/dotnet") && PythonRuntimeAdapter.TestCommand("/usr/share/dotnet/dotnet", "--version"))
            return "/usr/share/dotnet/dotnet";
        return null;
    }

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        var exe = FindDotNetExecutable();
        if (exe != null)
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        missingReason = "系统中未检测到 .NET SDK，请先安装 .NET 10 SDK 或在极速开发镜像中直接运行。";
        missingRunners = ["dotnet"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install Microsoft.DotNet.SDK.10",
        "alpine" => "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet",
        "debian" => "apt update && apt install -y dotnet-sdk-10.0 || apt install -y dotnet-sdk-8.0",
        "ubuntu" => "apt update && apt install -y dotnet-sdk-10.0 || apt install -y dotnet-sdk-8.0",
        "rhel" => "dnf install -y dotnet-sdk-10.0 || dnf install -y dotnet-sdk-8.0",
        _ => "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var entry = Path.Combine(pluginDir, manifest.Entrypoint);
        string args;
        if (entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            args = $"run --project \"{entry}\"";
        }
        else if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            args = $"exec \"{entry}\"";
        }
        else
        {
            var proj = Directory.GetFiles(pluginDir, "*.csproj").FirstOrDefault();
            if (proj == null && entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var autoProj = Path.Combine(pluginDir, "App.csproj");
                if (!File.Exists(autoProj))
                {
                    File.WriteAllText(autoProj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""");
                }
                proj = autoProj;
            }
            args = proj != null ? $"run --project \"{proj}\"" : $"run --project \"{pluginDir}\"";
        }

        var dotnetExe = FindDotNetExecutable() ?? "dotnet";
        return new PluginStartSpec
        {
            FileName = dotnetExe,
            Arguments = args,
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// Shell (Bash/sh) 运行时适配器。
/// </summary>
public sealed class ShellRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Bash;
    public string PrimaryExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bash" : "/bin/bash";
    public IReadOnlyList<string> CandidateExecutables => ["bash", "sh"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (PythonRuntimeAdapter.TestCommand("bash", "--version") || PythonRuntimeAdapter.TestCommand("sh", "--version"))
            {
                missingReason = null;
                missingRunners = [];
                return true;
            }
            missingReason = "Windows 环境下未找到 bash.exe (需 WSL 或 Git for Windows)。";
            missingRunners = ["bash"];
            return false;
        }

        if (File.Exists("/bin/bash") || File.Exists("/bin/sh"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }

        missingReason = "系统中未检测到可用的 Shell 解析器 (/bin/bash 或 /bin/sh)。";
        missingRunners = ["/bin/bash"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install Git.Git",
        "alpine" => "apk add --no-cache bash",
        _ => "apt update && apt install -y bash"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var entry = Path.Combine(pluginDir, manifest.Entrypoint);
        if (File.Exists(entry))
        {
            try
            {
                var content = File.ReadAllText(entry);
                if (content.Contains('\r'))
                {
                    File.WriteAllText(entry, content.Replace("\r\n", "\n").Replace('\r', '\n'));
                }
            }
            catch
            {
                // 忽略只读文件等清洗异常
            }
        }
        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bash" : (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");

        return new PluginStartSpec
        {
            FileName = shell,
            Arguments = $"\"{entry}\"",
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// PowerShell 运行时适配器。
/// </summary>
public sealed class PowerShellRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.PowerShell;
    public string PrimaryExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "pwsh";
    public IReadOnlyList<string> CandidateExecutables => ["pwsh", "powershell"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        if (PythonRuntimeAdapter.TestCommand("pwsh", "-version") || PythonRuntimeAdapter.TestCommand("powershell", "-v"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }

        missingReason = "系统中未检测到 PowerShell (pwsh/powershell)。";
        missingRunners = ["pwsh"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install Microsoft.PowerShell",
        "alpine" => "apk add --no-cache powershell",
        "rhel" => "dnf install -y powershell",
        _ => "apt update && apt install -y powershell"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var entry = Path.Combine(pluginDir, manifest.Entrypoint);
        var exe = PythonRuntimeAdapter.TestCommand("pwsh", "-version") ? "pwsh" : "powershell";

        return new PluginStartSpec
        {
            FileName = exe,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{entry}\"",
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// 原生已编译程序运行时适配器。
/// </summary>
public sealed class NativeRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Native;
    public PluginRuntime Runtime => RuntimeType;
    public string PrimaryExecutable => "native";
    public IReadOnlyList<string> CandidateExecutables => ["native"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        missingReason = null;
        missingRunners = [];
        return true;
    }

    public string ResolveInstallGuide(string distroKey) => "原生程序已内置或依赖系统包，请参考插件说明文档。";

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var targetRef = !string.IsNullOrWhiteSpace(manifest.ProgramRef) ? manifest.ProgramRef : manifest.Entrypoint;

        string exe;
        if (string.Equals(manifest.Source, "package", StringComparison.OrdinalIgnoreCase))
        {
            var fullPluginDir = Path.GetFullPath(pluginDir);
            var candidate = Path.GetFullPath(Path.Combine(pluginDir, targetRef));
            if (!candidate.StartsWith(fullPluginDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new System.Security.SecurityException($"禁止路径逃逸: {targetRef}");
            }
            if (File.Exists(candidate))
            {
                exe = candidate;
            }
            else if (NodeRuntimeAdapter.IsExecutableInPath(targetRef))
            {
                exe = targetRef;
            }
            else
            {
                // 三级自愈探查：检查宿主静态模板目录 /app/plugins/<id>/<targetRef>
                var templateCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "plugins", manifest.Id, targetRef));
                if (!File.Exists(templateCandidate))
                {
                    var parentDir = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
                    if (parentDir != null)
                    {
                        var alt = Path.Combine(parentDir, "plugins", manifest.Id, targetRef);
                        if (File.Exists(alt)) templateCandidate = alt;
                    }
                }

                if (File.Exists(templateCandidate))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                        File.Copy(templateCandidate, candidate, overwrite: true);
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                File.SetUnixFileMode(candidate,
                                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                            }
                            catch { }
                        }
                        exe = candidate;
                    }
                    catch
                    {
                        throw new FileNotFoundException($"原生程序文件不存在: {candidate}");
                    }
                }
                else
                {
                    throw new FileNotFoundException($"原生程序文件不存在: {candidate}");
                }
            }
        }
        else
        {
            var entry = Path.Combine(pluginDir, targetRef);
            exe = File.Exists(entry) ? Path.GetFullPath(entry) : targetRef;
        }

        if (File.Exists(exe))
        {
            var (isValid, reason) = NativeBinaryValidator.ValidateExecutable(exe);
            if (!isValid)
            {
                throw new InvalidOperationException($"原生程序二进制格式校验失败: {reason} (路径: {exe})");
            }
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(exe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }
            }
        }

        var portStr = port?.ToString() ?? "0";
        var listenHost = "127.0.0.1";
        var listenAddr = $"{listenHost}:{portStr}";
        var volumeDir = env.TryGetValue("LWT_VOLUME_DIR", out var vDir) ? vDir : pluginDir;
        var stateDir = env.TryGetValue("LWT_STATE_DIR", out var sDir) ? sDir : Path.Combine(volumeDir, "state");
        var runDir = env.TryGetValue("LWT_RUN_DIR", out var rDir) ? rDir : Path.Combine(volumeDir, "run");
        var resolvedArgs = new List<string>();
        if (manifest.Arguments != null)
        {
            for (int i = 0; i < manifest.Arguments.Count; i++)
            {
                var arg = manifest.Arguments[i];
                if (arg is "--control-token-file" or "-control-token-file")
                {
                    if (i + 1 < manifest.Arguments.Count && manifest.Arguments[i + 1].Contains("{controlTokenFile}", StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                        continue;
                    }
                }
                if (arg.Contains("{controlTokenFile}", StringComparison.OrdinalIgnoreCase)) continue;

                arg = arg.Replace("0.0.0.0", "127.0.0.1", StringComparison.OrdinalIgnoreCase);

                resolvedArgs.Add(arg
                    .Replace("{port}", portStr, StringComparison.OrdinalIgnoreCase)
                    .Replace("{listenAddr}", listenAddr, StringComparison.OrdinalIgnoreCase)
                    .Replace("{listen}", listenAddr, StringComparison.OrdinalIgnoreCase)
                    .Replace("{pluginDir}", pluginDir, StringComparison.OrdinalIgnoreCase)
                    .Replace("{volumeDir}", volumeDir, StringComparison.OrdinalIgnoreCase)
                    .Replace("{stateDir}", stateDir, StringComparison.OrdinalIgnoreCase)
                    .Replace("{runDir}", runDir, StringComparison.OrdinalIgnoreCase)
                    .Replace("{runId}", Guid.NewGuid().ToString("N")[..8], StringComparison.OrdinalIgnoreCase));
            }
        }

        var args = resolvedArgs.Count > 0 ? string.Join(" ", resolvedArgs) : "";

        return new PluginStartSpec
        {
            FileName = exe,
            Arguments = args,
            ArgumentList = resolvedArgs,
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}

