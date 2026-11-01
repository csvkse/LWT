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
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LWT_PLUGIN_ID"] = manifest.Id,
            ["LWT_DATA_DIR"] = pluginDir,
            ["LWT_SQLITE_PATH"] = Path.Combine(pluginDir, "data.db"),
            ["LWT_KV_PATH"] = Path.Combine(pluginDir, "kv.db")
        };

        if (port.HasValue)
        {
            var portKey = string.IsNullOrWhiteSpace(manifest.Network?.PortEnv) ? "PORT" : manifest.Network.PortEnv;
            dict[portKey] = port.Value.ToString();
        }

        if (manifest.Environment != null)
        {
            foreach (var (k, v) in manifest.Environment)
            {
                dict[k] = v;
            }
        }

        if (extraEnv != null)
        {
            foreach (var (k, v) in extraEnv)
            {
                dict[k] = v;
            }
        }

        return dict;
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
    public string PrimaryExecutable => "dotnet";
    public IReadOnlyList<string> CandidateExecutables => ["dotnet"];

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        if (PythonRuntimeAdapter.TestCommand("dotnet", "--version"))
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        missingReason = "系统中未检测到 .NET 运行时或 SDK，请先安装 dotnet (如: sudo apt install dotnet-sdk-8.0 或更高版本)。";
        missingRunners = ["dotnet"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install Microsoft.DotNet.SDK.8",
        "alpine" => "apk add --no-cache dotnet8-sdk",
        "rhel" => "dnf install -y dotnet-sdk-8.0",
        _ => "apt update && apt install -y dotnet-sdk-8.0"
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
    <TargetFramework>net8.0</TargetFramework>
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

        return new PluginStartSpec
        {
            FileName = "dotnet",
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
                throw new FileNotFoundException($"原生程序文件不存在: {candidate}");
            }
        }
        else
        {
            var entry = Path.Combine(pluginDir, targetRef);
            exe = File.Exists(entry) ? Path.GetFullPath(entry) : targetRef;
        }

        var args = manifest.Arguments.Count > 0 ? string.Join(" ", manifest.Arguments) : "";

        return new PluginStartSpec
        {
            FileName = exe,
            Arguments = args,
            ArgumentList = manifest.Arguments,
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}
