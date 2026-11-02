using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PluginDependenciesTests
{
    [Fact]
    public void PluginDependencies_CanDeserializeFromObjectFormat()
    {
        var json = """
        {
            "runners": ["node"],
            "binaries": ["caddy"]
        }
        """;

        var deps = JsonSerializer.Deserialize<PluginDependencies>(json);
        Assert.NotNull(deps);
        Assert.Contains("node", deps.Runners);
        Assert.Contains("caddy", deps.Binaries);
        Assert.Empty(deps.Items);
    }

    [Fact]
    public void PluginDependencies_CanDeserializeFromArrayFormat()
    {
        var json = """
        [
            {
                "id": "node-ecosystem",
                "name": "Node.js & npx 工具链",
                "required": false,
                "recommended": true,
                "description": "用于执行 GitHub MCP 服务",
                "probe": {
                    "command": "npx",
                    "args": ["--version"]
                },
                "installGuide": {
                    "alpine": "apk add --no-cache nodejs npm",
                    "debian": "apt update && apt install -y nodejs npm"
                }
            },
            {
                "id": "python-uv",
                "name": "Python 3 & uv/uvx",
                "required": true,
                "recommended": true,
                "description": "用于执行 Fetch MCP 服务"
            }
        ]
        """;

        var deps = JsonSerializer.Deserialize<PluginDependencies>(json);
        Assert.NotNull(deps);
        Assert.Equal(2, deps.Items.Count);

        var first = deps.Items[0];
        Assert.Equal("node-ecosystem", first.Id);
        Assert.Equal("Node.js & npx 工具链", first.Name);
        Assert.False(first.Required);
        Assert.True(first.Recommended);
        Assert.NotNull(first.Probe);
        Assert.Equal("npx", first.Probe.Command);
        Assert.Contains("--version", first.Probe.Args);
        Assert.NotNull(first.InstallGuide);
        Assert.Equal("apk add --no-cache nodejs npm", first.InstallGuide["alpine"]);

        var second = deps.Items[1];
        Assert.Equal("python-uv", second.Id);
        Assert.True(second.Required);
    }

    [Fact]
    public void PluginManifest_GetUnifiedDependencies_MergesProperly()
    {
        var manifest = new PluginManifest
        {
            Id = "test-plugin",
            Name = "Test Plugin",
            Dependencies = new PluginDependencies
            {
                Binaries = ["last", "who"]
            }
        };

        var unified = manifest.GetUnifiedDependencies();
        Assert.Equal(2, unified.Count);
        Assert.Equal("last", unified[0].Id);
        Assert.Equal("who", unified[1].Id);
        Assert.True(unified[0].Required);
    }

    [Fact]
    public void PluginManifest_CanDeserializeWithPluginJsonContext()
    {
        var json = """
        {
            "manifestVersion": 1,
            "id": "mcp-bridge",
            "name": "MCP 智能安全网关桥接服务",
            "runtime": "native",
            "dependencies": [
                {
                    "id": "node-ecosystem",
                    "name": "Node.js & npx 工具链",
                    "required": false,
                    "recommended": true,
                    "description": "用于执行 GitHub MCP 服务",
                    "probe": {
                        "command": "npx",
                        "args": ["--version"]
                    },
                    "installGuide": {
                        "alpine": "apk add --no-cache nodejs npm",
                        "debian": "apt update && apt install -y nodejs npm"
                    }
                }
            ]
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, LinuxWebTool.Infrastructure.Features.Plugins.Adapters.PluginJsonContext.Default.PluginManifest);
        Assert.NotNull(manifest);
        Assert.NotNull(manifest.Dependencies);
        Assert.Single(manifest.Dependencies.Items);
        Assert.Equal("node-ecosystem", manifest.Dependencies.Items[0].Id);
    }
}
