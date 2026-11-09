using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// WebAssembly (Wasm) 多语言运行时统一适配器。
/// 支持宿主环境 Wasmtime 或 Node.js WASI / WebAssembly 运行环境。
/// </summary>
public sealed class WasmRuntimeAdapter : IRuntimeAdapter
{
    public PluginRuntime RuntimeType => PluginRuntime.Wasm;
    public PluginRuntime Runtime => RuntimeType;
    public string PrimaryExecutable => "wasmtime";
    public IReadOnlyList<string> CandidateExecutables => ["wasmtime", "node"];

    public bool CheckAvailable(out string? missingReason)
    {
        return CheckAvailable(out missingReason, out _);
    }

    public bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners)
    {
        var nodeAvailable = NodeRuntimeAdapter.IsExecutableInPath("node");
        var wasmtimeAvailable = NodeRuntimeAdapter.IsExecutableInPath("wasmtime");

        if (wasmtimeAvailable || nodeAvailable)
        {
            missingReason = null;
            missingRunners = [];
            return true;
        }

        missingReason = "未检测到 WebAssembly 执行宿主环境（支持 wasmtime 或 node）";
        missingRunners = ["wasmtime", "node"];
        return false;
    }

    public string ResolveInstallGuide(string distroKey) =>
        distroKey switch
        {
            "alpine" => "apk add nodejs # 或 curl https://wasmtime.dev/install.sh -sSf | bash",
            "debian" => "apt install -y nodejs # 或 curl https://wasmtime.dev/install.sh -sSf | bash",
            "arch" => "pacman -S wasmtime nodejs",
            "windows" => "winget install BytecodeAlliance.Wasmtime 或 winget install OpenJS.NodeJS",
            _ => "curl https://wasmtime.dev/install.sh -sSf | bash"
        };

    public PluginStartSpec BuildStartSpec(
        PluginManifest manifest,
        string pluginDir,
        int? port,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var entry = string.IsNullOrWhiteSpace(manifest.Entrypoint) ? "main.wasm" : manifest.Entrypoint;
        return new PluginStartSpec
        {
            FileName = "wasmtime",
            Arguments = $"run \"{entry}\"",
            WorkingDirectory = pluginDir,
            EnvironmentVariables = extraEnv ?? new Dictionary<string, string>()
        };
    }
}
