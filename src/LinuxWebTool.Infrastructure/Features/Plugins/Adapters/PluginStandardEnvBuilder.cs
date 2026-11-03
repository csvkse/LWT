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

        // 历史平滑自愈迁移：若旧版路径下有数据文件，自动自愈同步至持久卷
        MigrateLegacyPluginData(manifest.Id, pluginDir, volumeDir, stateDir);

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
