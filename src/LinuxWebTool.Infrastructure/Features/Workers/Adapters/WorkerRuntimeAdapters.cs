using System.Diagnostics;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers.Adapters;

/// <summary>
/// Node.js 运行时适配器。
/// </summary>
public sealed class NodeRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.Node;

    public bool CheckAvailable(out string? missingReason)
    {
        if (IsExecutableInPath("node"))
        {
            missingReason = null;
            return true;
        }
        missingReason = "系统中未检测到 Node.js 运行时，请先安装 node (如: sudo apt install nodejs 或 nvm)。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = BuildStandardEnv(manifest, workerDir, port, extraEnv);
        var entry = Path.Combine(workerDir, manifest.Entrypoint);
        return new WorkerStartSpec
        {
            FileName = "node",
            Arguments = $"\"{entry}\"",
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }

    private static bool IsExecutableInPath(string exe)
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

    internal static Dictionary<string, string> BuildStandardEnv(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LWT_WORKER_ID"] = manifest.Id,
            ["LWT_DATA_DIR"] = workerDir,
            ["LWT_SQLITE_PATH"] = Path.Combine(workerDir, "data.db"),
            ["LWT_KV_PATH"] = Path.Combine(workerDir, "kv.db")
        };

        if (port.HasValue)
        {
            var portKey = string.IsNullOrWhiteSpace(manifest.Network.PortEnv) ? "PORT" : manifest.Network.PortEnv;
            dict[portKey] = port.Value.ToString();
        }

        foreach (var (k, v) in manifest.EnvironmentVariables)
        {
            dict[k] = v;
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
public sealed class PythonRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.Python;

    public bool CheckAvailable(out string? missingReason)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        if (TestCommand(exe, "--version"))
        {
            missingReason = null;
            return true;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TestCommand("py", "--version"))
        {
            missingReason = null;
            return true;
        }
        missingReason = "系统中未检测到 Python 3 运行时，请先安装 python3 (如: sudo apt install python3)。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, workerDir, port, extraEnv);
        env["PYTHONUNBUFFERED"] = "1"; // 确保 Python 标准输出无缓冲流入日志流

        // 优先探测本地虚拟环境 .venv
        string exe = DetectPythonExecutable(workerDir);
        var entry = Path.Combine(workerDir, manifest.Entrypoint);

        return new WorkerStartSpec
        {
            FileName = exe,
            Arguments = $"-u \"{entry}\"",
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }

    private static string DetectPythonExecutable(string workerDir)
    {
        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var venvPy = isWin
            ? Path.Combine(workerDir, ".venv", "Scripts", "python.exe")
            : Path.Combine(workerDir, ".venv", "bin", "python3");

        if (File.Exists(venvPy)) return venvPy;
        return isWin ? "python" : "python3";
    }

    private static bool TestCommand(string exe, string args)
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
/// .NET Core C# 运行时适配器（支持顶层单文件 dotnet run Program.cs）。
/// </summary>
public sealed class DotNetRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.DotNet;

    public bool CheckAvailable(out string? missingReason)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null && proc.WaitForExit(3000) && proc.ExitCode == 0)
            {
                missingReason = null;
                return true;
            }
        }
        catch { }

        missingReason = "系统中未检测到 .NET SDK 运行时环境，请先安装 .NET Core SDK。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, workerDir, port, extraEnv);
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var entry = Path.Combine(workerDir, manifest.Entrypoint);
        var isDll = manifest.Entrypoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var isProject = manifest.Entrypoint.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

        var args = isDll ? $"\"{entry}\"" : (isProject ? $"run --project \"{entry}\"" : $"run \"{entry}\"");
        return new WorkerStartSpec
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// Shell (Bash) 运行时适配器。
/// </summary>
public sealed class ShellRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.Bash;

    public bool CheckAvailable(out string? missingReason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            missingReason = null;
            return true;
        }
        if (File.Exists(@"C:\Program Files\Git\bin\bash.exe") || File.Exists(@"C:\Windows\System32\bash.exe"))
        {
            missingReason = null;
            return true;
        }
        missingReason = "Windows 环境下未找到 bash 解释器，请安装 Git for Windows 或 WSL。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, workerDir, port, extraEnv);
        var entry = Path.Combine(workerDir, manifest.Entrypoint);

        string exe = "/bin/bash";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (File.Exists(@"C:\Program Files\Git\bin\bash.exe")) exe = @"C:\Program Files\Git\bin\bash.exe";
            else exe = "bash";
        }

        return new WorkerStartSpec
        {
            FileName = exe,
            Arguments = $"\"{entry}\"",
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// PowerShell 运行时适配器。
/// </summary>
public sealed class PowerShellRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.PowerShell;

    public bool CheckAvailable(out string? missingReason)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            missingReason = null;
            return true;
        }
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = "-Version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null && proc.WaitForExit(3000) && proc.ExitCode == 0)
            {
                missingReason = null;
                return true;
            }
        }
        catch { }
        missingReason = "Linux 环境下未检测到 PowerShell Core (pwsh)，请先安装 powershell。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, workerDir, port, extraEnv);
        var entry = Path.Combine(workerDir, manifest.Entrypoint);

        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exe = isWin ? "powershell.exe" : "pwsh";

        return new WorkerStartSpec
        {
            FileName = exe,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{entry}\"",
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }
}

/// <summary>
/// Windows CMD 批处理运行时适配器。
/// </summary>
public sealed class BatchRuntimeAdapter : IWorkerRuntimeAdapter
{
    public WorkerRuntimeType RuntimeType => WorkerRuntimeType.Batch;

    public bool CheckAvailable(out string? missingReason)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            missingReason = null;
            return true;
        }
        missingReason = "Batch (.bat/.cmd) 批处理仅支持在 Windows 系统下执行。";
        return false;
    }

    public WorkerStartSpec BuildStartSpec(WorkerManifest manifest, string workerDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, workerDir, port, extraEnv);
        var entry = Path.Combine(workerDir, manifest.Entrypoint);
        return new WorkerStartSpec
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{entry}\"",
            WorkingDirectory = workerDir,
            EnvironmentVariables = env
        };
    }
}
