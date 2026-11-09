using System.Diagnostics;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 插件标准环境变量与宿主元数据向下投影构建器 (Host Metadata Downward Projection Builder)
/// 遵循 Kubernetes Downward API 与 Linux systemd 标准，提供网络拓扑、存储卷隔离、硬件能力与安全凭据的五维向下投影。
/// </summary>
public static class PluginStandardEnvBuilder
{
    public static Dictionary<string, string> BuildStandardEnv(
        PluginManifest manifest,
        string pluginDir,
        int? port,
        IReadOnlyDictionary<string, string>? extraEnv)
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

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 维度 2: 存储隔离与持久卷
            ["LWT_PLUGIN_ID"] = manifest.Id,
            ["LWT_CODE_DIR"] = pluginDir,
            ["LWT_VOLUME_DIR"] = volumeDir,
            ["LWT_STATE_DIR"] = stateDir,
            ["LWT_RUN_DIR"] = runDir,
            ["LWT_DATA_DIR"] = volumeDir, // 兼容历史别名
            ["LWT_SQLITE_PATH"] = Path.Combine(volumeDir, "data.db"),
            ["LWT_KV_PATH"] = Path.Combine(volumeDir, "kv.db"),
            ["HOST"] = "127.0.0.1",
            ["BIND_IP"] = "127.0.0.1",
            ["BIND_ADDRESS"] = "127.0.0.1"
        };

        if (manifest.Layout != null)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Layout.SourceEntry))
                dict["LWT_SOURCE_ENTRY"] = manifest.Layout.SourceEntry;
            if (!string.IsNullOrWhiteSpace(manifest.Layout.TargetArtifact))
                dict["LWT_TARGET_ARTIFACT"] = manifest.Layout.TargetArtifact;
            dict["LWT_STORAGE_DIR"] = Path.Combine(pluginDir, manifest.Layout.StorageDir);
            dict["LWT_RUNTIME_DIR"] = Path.Combine(pluginDir, manifest.Layout.RuntimeDir);
        }

        // 维度 1: 宿主网络与反代拓扑元数据 (双端口模型)
        var hostInternalPort = Environment.GetEnvironmentVariable("LWT_HOST_INTERNAL_PORT");
        if (string.IsNullOrWhiteSpace(hostInternalPort))
        {
            var aspnetUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? Environment.GetEnvironmentVariable("URLS") ?? "";
            hostInternalPort = aspnetUrls.Contains(":5270") ? "5270" : "5270";
        }

        var hostExternalPort = Environment.GetEnvironmentVariable("LWT_HOST_EXTERNAL_PORT");
        if (string.IsNullOrWhiteSpace(hostExternalPort))
        {
            hostExternalPort = hostInternalPort;
        }

        var hostPublicIp = Environment.GetEnvironmentVariable("LWT_HOST_PUBLIC_IP");
        if (string.IsNullOrWhiteSpace(hostPublicIp))
        {
            hostPublicIp = "127.0.0.1";
        }

        dict["LWT_HOST_INTERNAL_PORT"] = hostInternalPort;
        dict["LWT_HOST_PORT"] = hostInternalPort; // 兼容标准别名
        dict["LWT_HOST_EXTERNAL_PORT"] = hostExternalPort;
        dict["LWT_HOST_PUBLIC_IP"] = hostPublicIp;
        dict["LWT_HOST_INTERNAL_URL"] = $"http://127.0.0.1:{hostInternalPort}";
        dict["LWT_HOST_PUBLIC_URL"] = $"http://{hostPublicIp}:{hostExternalPort}";
        dict["LWT_HOST_BASE_URL"] = $"http://{hostPublicIp}:{hostExternalPort}"; // 兼容标准别名
        dict["LWT_PLUGIN_INGRESS_PREFIX"] = $"/plugins/{manifest.Id}";
        dict["LWT_INGRESS_PREFIX"] = $"/plugins/{manifest.Id}"; // 兼容标准别名

        // 维度 3: 硬件与操作系统特权能力
        dict["LWT_HOST_OS"] = OperatingSystem.IsLinux() ? "linux" : (OperatingSystem.IsWindows() ? "windows" : "macos");
        dict["LWT_HOST_DISTRO"] = PluginManager.DetectCurrentDistroKey();
        dict["LWT_RUNTIME_DISTRO"] = dict["LWT_HOST_DISTRO"]; // 兼容标准别名
        dict["LWT_HOST_ARCH"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        dict["LWT_HAS_TUN"] = File.Exists("/dev/net/tun") ? "true" : "false";

        // 维度 4: 平台身份与环境模式
        dict["LWT_VERSION"] = "1.0.0";
        dict["LWT_ENV_MODE"] = hostExternalPort == "5271" ? "dev" : "production";

        // 微服务分配端口
        if (port.HasValue)
        {
            var portKey = string.IsNullOrWhiteSpace(manifest.Network?.PortEnv) ? "PORT" : manifest.Network.PortEnv;
            dict[portKey] = port.Value.ToString();
            dict["LWT_PLUGIN_PORT"] = port.Value.ToString();
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
}
