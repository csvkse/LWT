using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// Go 语言动态运行引擎适配器：
/// - 支持定位系统 PATH 与标准安装路径中的 go 编译器；
/// - 提供两阶段 JIT 增量构建缓存（JIT Incremental Build Cache）；
/// - 杜绝 `go run` 带来的父子孤儿进程与端口泄漏隐患，直接调度产物二进制；
/// - 自动解析动态回环端口与路径宏变量。
/// </summary>
public sealed class GoRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Go;
    public string PrimaryExecutable => FindGoExecutable() ?? "go";
    public IReadOnlyList<string> CandidateExecutables => ["go", "/usr/local/go/bin/go", "/usr/lib/go/bin/go", "/usr/bin/go"];

    public static string? FindGoExecutable()
    {
        if (PythonRuntimeAdapter.TestCommand("go", "version")) return "go";
        var candidates = new[] { "/usr/local/go/bin/go", "/usr/lib/go/bin/go", "/usr/bin/go" };
        foreach (var c in candidates)
        {
            if (File.Exists(c) && PythonRuntimeAdapter.TestCommand(c, "version")) return c;
        }
        return null;
    }

    public bool CheckAvailable(out string? missingReason) => CheckAvailable(out missingReason, out _);

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        var exe = FindGoExecutable();
        if (exe != null)
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }
        missingReason = "系统中未检测到 Go 编译器环境，请先安装 Go SDK (如: apk add go 或 apt install -y golang-go)。";
        missingRunners = ["go"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) => distroKey switch
    {
        "windows" => "winget install GoLang.Go",
        "alpine" => "apk update && apk add --no-cache go",
        "debian" => "apt update && apk install -y golang-go",
        "ubuntu" => "apt update && apk install -y golang-go",
        "rhel" => "dnf install -y golang",
        _ => "apt update && apt install -y golang-go || apk add --no-cache go"
    };

    public PluginStartSpec BuildStartSpec(PluginManifest manifest, string pluginDir, int? port, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, pluginDir, port, extraEnv);
        var goExe = FindGoExecutable();
        if (goExe == null)
        {
            throw new FileNotFoundException("系统中未检测到 Go 编译器环境，无法执行动态编译与启动。");
        }

        var entry = Path.Combine(pluginDir, manifest.Entrypoint);
        var isSource = entry.EndsWith(".go", StringComparison.OrdinalIgnoreCase) || Directory.Exists(entry);

        string exeToRun;
        if (isSource)
        {
            var cacheDir = Path.Combine(pluginDir, ".cache", "bin");
            Directory.CreateDirectory(cacheDir);

            var binName = OperatingSystem.IsWindows() ? "app.exe" : "app";
            var targetBinary = Path.Combine(cacheDir, binName);
            var stampFile = Path.Combine(cacheDir, ".source.stamp");

            var sourceFiles = Directory.GetFiles(pluginDir, "*.go", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".cache", StringComparison.OrdinalIgnoreCase) && !f.Contains("vendor", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            long combinedStamp = 0;
            foreach (var sf in sourceFiles)
            {
                var fi = new FileInfo(sf);
                combinedStamp ^= fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            }

            var needCompile = !File.Exists(targetBinary) || !File.Exists(stampFile);
            if (!needCompile)
            {
                if (long.TryParse(File.ReadAllText(stampFile), out var recordedStamp) && recordedStamp != combinedStamp)
                {
                    needCompile = true;
                }
            }

            if (needCompile)
            {
                string buildTarget;
                if (Directory.Exists(entry))
                {
                    var relDir = Path.GetRelativePath(pluginDir, entry).Replace('\\', '/').TrimStart('/');
                    buildTarget = string.IsNullOrEmpty(relDir) || relDir == "." ? "./." : $"./{relDir}";
                }
                else
                {
                    var relEntry = Path.GetRelativePath(pluginDir, entry);
                    var targetDir = Path.GetDirectoryName(relEntry)?.Replace('\\', '/').TrimStart('/');
                    buildTarget = string.IsNullOrEmpty(targetDir) || targetDir == "." ? "./." : $"./{targetDir}";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = goExe,
                    Arguments = $"build -ldflags \"-s -w\" -o \"{targetBinary}\" {buildTarget}",
                    WorkingDirectory = pluginDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["CGO_ENABLED"] = "0";

                using var buildProc = Process.Start(psi) ?? throw new InvalidOperationException("启动 Go 增量构建进程失败。");
                var stdout = buildProc.StandardOutput.ReadToEnd();
                var stderr = buildProc.StandardError.ReadToEnd();
                buildProc.WaitForExit(30000);

                if (buildProc.ExitCode != 0 || !File.Exists(targetBinary))
                {
                    throw new InvalidOperationException($"Go 动态增量编译失败 (ExitCode {buildProc.ExitCode}): {stderr} {stdout}".Trim());
                }

                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(targetBinary,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch { }
                }

                File.WriteAllText(stampFile, combinedStamp.ToString());
            }

            exeToRun = targetBinary;
        }
        else
        {
            exeToRun = File.Exists(entry) ? Path.GetFullPath(entry) : entry;
        }

        var portStr = port?.ToString() ?? "0";
        var isPortMode = manifest.Network?.Mode == "port";
        var listenHost = isPortMode ? "0.0.0.0" : "127.0.0.1";
        var listenAddr = $"{listenHost}:{portStr}";
        var volumeDir = env.TryGetValue("LWT_VOLUME_DIR", out var vDir) ? vDir : pluginDir;
        var stateDir = env.TryGetValue("LWT_STATE_DIR", out var sDir) ? sDir : Path.Combine(volumeDir, "state");
        var runDir = env.TryGetValue("LWT_RUN_DIR", out var rDir) ? rDir : string.Empty;
        var resolvedArgs = new List<string>();
        for (int i = 0; i < manifest.Arguments.Count; i++)
        {
            var arg = manifest.Arguments[i];
            if (!isPortMode)
            {
                arg = arg.Replace("0.0.0.0", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            }
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

        var args = resolvedArgs.Count > 0 ? string.Join(" ", resolvedArgs) : "";

        return new PluginStartSpec
        {
            FileName = exeToRun,
            Arguments = args,
            ArgumentList = resolvedArgs,
            WorkingDirectory = pluginDir,
            EnvironmentVariables = env
        };
    }
}
