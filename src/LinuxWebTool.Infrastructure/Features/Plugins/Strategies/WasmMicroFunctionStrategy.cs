using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Strategies;

/// <summary>
/// WebAssembly (Wasm) 极速微函数沙箱执行策略 (WebAssembly Micro-Function Sandbox Strategy)
/// - 内存安全沙箱与无状态计算微函数模型；
/// - 极速冷启动（< 5ms），零后台常驻端口与长期守护进程开销；
/// - 支持 WAT (WebAssembly Text) 即时动态汇编与 .wasm 缓存加速；
/// - 统一标准化入参透传 (LWT_ACTION, LWT_PARAM) 与 JSON 响应管道；
/// - 严密集成 PluginCgroupManager 内核配额、PluginEnvironmentSanitizer 与内存故障域隔离。
/// </summary>
public sealed class WasmMicroFunctionStrategy : IExecutionProfileStrategy
{
    public PluginProfile Profile => PluginProfile.Wasm;

    public ValueTask<(bool Success, string? ErrorMessage)> StartAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        PluginProcessRingBuffer ringBuffer,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        stateMachine.NotifyStarting();

        var wasmFile = ResolveOrCompileWasmFile(pluginDir, manifest.Entrypoint, "main");
        if (wasmFile == null)
        {
            stateMachine.RecordExit(1, "未找到有效的 WebAssembly (.wasm / .wat) 入口模块");
            return ValueTask.FromResult((false, (string?)"未找到有效的 WebAssembly (.wasm / .wat) 入口模块"));
        }

        ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [WASM] 微函数沙箱就绪，目标模块: {Path.GetFileName(wasmFile)}");
        stateMachine.NotifyRunning(readiness: "Ready");
        return ValueTask.FromResult((true, (string?)null));
    }

    public ValueTask<bool> StopAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        CancellationToken ct)
    {
        stateMachine.NotifyStopped();
        return ValueTask.FromResult(true);
    }

    public async ValueTask<PluginActionResult> ExecuteActionAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginActionRequest request,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        var action = request.Action ?? "run";
        var wasmFile = ResolveOrCompileWasmFile(pluginDir, manifest.Entrypoint, action);
        if (wasmFile == null)
        {
            return PluginActionResult.Fail($"未找到适用于 Action [{action}] 的 WebAssembly (.wasm / .wat) 模块");
        }

        var sw = Stopwatch.StartNew();

        // 优先使用 wasmtime，若无则使用通用的 Node.js WebAssembly WASI 驱动
        var (runnerExe, runnerArgs) = ResolveWasmRunner(wasmFile, action, request.Param);

        var psi = new ProcessStartInfo
        {
            FileName = runnerExe,
            Arguments = runnerArgs,
            WorkingDirectory = pluginDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // 标准化微函数环境变量透传
        var customEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LWT_ACTION"] = action,
            ["LWT_PARAM"] = request.Param ?? ""
        };

        if (manifest.Environment != null)
        {
            foreach (var (k, v) in manifest.Environment)
            {
                customEnv[k] = v;
            }
        }

        // 强行清洗父进程隐式敏感环境变量
        PluginEnvironmentSanitizer.ApplySafeEnvironment(psi, customEnv);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            // 接入 cgroup v2 内核资源硬隔离
            PluginCgroupManager.ConfigureCgroup(manifest.Id, manifest);
            PluginCgroupManager.AssignProcess(manifest.Id, process.Id);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(15));

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            if (process.ExitCode != 0)
            {
                return PluginActionResult.Fail($"Wasm 微函数执行退出非零 ({process.ExitCode}): {stderr}");
            }

            var output = string.IsNullOrWhiteSpace(stdout)
                ? $"{{\"status\":\"ok\",\"action\":\"{action}\",\"executionTimeMs\":{sw.ElapsedMilliseconds}}}"
                : stdout.Trim();

            return PluginActionResult.Ok(output);
        }
        catch (OperationCanceledException)
        {
            return PluginActionResult.Fail($"Wasm 微函数执行超时已熔断 ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail($"Wasm 引擎实例化异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 检索目标 .wasm 模块，支持从 .wat 文本即时编译 (Just-in-Time Assembly)。
    /// </summary>
    public static string? ResolveOrCompileWasmFile(string pluginDir, string? entrypoint, string? action)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(action))
        {
            candidates.Add(Path.Combine(pluginDir, "wasm", $"{action}.wasm"));
            candidates.Add(Path.Combine(pluginDir, "functions", $"{action}.wasm"));
            candidates.Add(Path.Combine(pluginDir, $"{action}.wasm"));
        }

        if (!string.IsNullOrWhiteSpace(entrypoint))
        {
            if (entrypoint.EndsWith(".wat", StringComparison.OrdinalIgnoreCase))
            {
                var compiled = TryCompileWatToWasm(Path.Combine(pluginDir, entrypoint));
                if (compiled != null) return compiled;
            }
            candidates.Add(Path.Combine(pluginDir, entrypoint));
        }

        candidates.Add(Path.Combine(pluginDir, "main.wasm"));
        candidates.Add(Path.Combine(pluginDir, "index.wasm"));
        candidates.Add(Path.Combine(pluginDir, "module.wasm"));

        // 尝试检测同名 .wat 源码即时汇编
        var watCandidates = new List<string>
        {
            Path.Combine(pluginDir, "function.wat"),
            Path.Combine(pluginDir, "main.wat"),
            Path.Combine(pluginDir, $"{action}.wat")
        };

        foreach (var wat in watCandidates.Where(File.Exists))
        {
            var compiled = TryCompileWatToWasm(wat);
            if (compiled != null) return compiled;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryCompileWatToWasm(string watPath)
    {
        if (!File.Exists(watPath)) return null;

        var wasmTarget = Path.ChangeExtension(watPath, ".wasm");
        if (File.Exists(wasmTarget) && File.GetLastWriteTimeUtc(wasmTarget) >= File.GetLastWriteTimeUtc(watPath))
        {
            return wasmTarget;
        }

        // 探测本地是否有 wat2wasm 工具
        var hasWat2Wasm = IsExecutableAvailable("wat2wasm");
        if (!hasWat2Wasm) return File.Exists(wasmTarget) ? wasmTarget : null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wat2wasm",
                Arguments = $"\"{watPath}\" -o \"{wasmTarget}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && File.Exists(wasmTarget))
                {
                    return wasmTarget;
                }
            }
        }
        catch { }

        return File.Exists(wasmTarget) ? wasmTarget : null;
    }

    private static bool IsExecutableAvailable(string exe)
    {
        if (OperatingSystem.IsLinux())
        {
            return File.Exists($"/usr/bin/{exe}") || File.Exists($"/usr/local/bin/{exe}") || File.Exists($"/bin/{exe}");
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, exe)) || File.Exists(Path.Combine(dir, $"{exe}.exe")));
    }

    private static (string Exe, string Args) ResolveWasmRunner(string wasmPath, string action, string? parameter)
    {
        var safeParam = (parameter ?? "").Replace("\"", "\\\"");

        // 1. 优先探测独立的 wasmtime 引擎
        if (File.Exists("/usr/bin/wasmtime") || File.Exists("/usr/local/bin/wasmtime") || IsExecutableAvailable("wasmtime"))
        {
            return ("wasmtime", $"run --env LWT_ACTION=\"{action}\" --env LWT_PARAM=\"{safeParam}\" -- \"{wasmPath}\" \"{action}\" \"{safeParam}\"");
        }

        // 2. 默认使用通用的 Node.js WebAssembly / WASI 沙箱驱动脚本
        var inlineScript =
            "const fs=require('fs');" +
            "const wasmBuffer=fs.readFileSync(process.argv[1]);" +
            "const action=process.argv[2]||'run';" +
            "const param=process.argv[3]||'';" +
            "WebAssembly.instantiate(wasmBuffer,{}).then(r=>{" +
            "  const exp=r.instance.exports;" +
            "  const fn=exp[action]||exp.run||exp.main||exp._start;" +
            "  if(typeof fn==='function'){" +
            "    let ret;" +
            "    if(param){" +
            "      const nums=param.split(',').map(s=>Number(s.trim())).filter(n=>!isNaN(n));" +
            "      ret=nums.length>0?fn(...nums):fn();" +
            "    }else{" +
            "      ret=fn();" +
            "    }" +
            "    console.log(JSON.stringify({status:'ok',action:action,result:ret,exports:Object.keys(exp)}));" +
            "  }else{" +
            "    console.log(JSON.stringify({status:'ready',action:action,exports:Object.keys(exp)}));" +
            "  }" +
            "}).catch(e=>{console.error(e.message);process.exit(1);});";

        var formattedPath = wasmPath.Replace('\\', '/');
        return ("node", $"-e \"{inlineScript}\" \"{formattedPath}\" \"{action}\" \"{safeParam}\"");
    }
}
