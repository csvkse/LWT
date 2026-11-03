namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

/// <summary>
/// 平台级通用插件 Ingress 声明式配置。
/// 业务中立，适用于网络服务、全双工双向流、AI MCP (SSE)、在线终端与 Web 控制台等全生态微服务。
/// </summary>
public sealed record PluginIngressConfig
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<PluginIngressRoute> Routes { get; init; } = [];
}

/// <summary>
/// 声明式 Ingress 路由规则定义（对齐 K8s Gateway API 与 YARP 规范）。
/// </summary>
public sealed record PluginIngressRoute
{
    public string Name { get; init; } = "";
    public IngressMatchConfig Match { get; init; } = new();
    public IngressTargetConfig Target { get; init; } = new();
    public IngressTransportConfig Transport { get; init; } = new();
    public IngressSecurityConfig Security { get; init; } = new();
    public IngressFallbackConfig Fallback { get; init; } = new();
}

public sealed record IngressMatchConfig
{
    public string Path { get; init; } = "/";
    public string PathType { get; init; } = "Prefix"; // "Prefix" | "Exact"
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public sealed record IngressTargetConfig
{
    public string Type { get; init; } = "allocatedPort"; // "allocatedPort" | "customPort"
    public string Destination { get; init; } = "127.0.0.1:{port}";
    public bool StripPrefix { get; init; }
}

public sealed record IngressTransportConfig
{
    public string Mode { get; init; } = "http_reverse_proxy"; // "http_reverse_proxy" | "duplex_stream" | "sse"
    public string BufferPolicy { get; init; } = "default"; // "default" | "unbuffered"
}

public sealed record IngressSecurityConfig
{
    public string Auth { get; init; } = "inherit"; // "inherit" | "platform_session" | "secret_token" | "none"
    public string TokenHeader { get; init; } = "X-LWT-Token";
    public string? TokenQuery { get; init; } = "token";
    public string? SecretValue { get; init; }
    public string AllowedSubnets { get; init; } = "*";
}

public sealed record IngressFallbackConfig
{
    public string OnUnauthorized { get; init; } = "reject_json"; // "reject_json" | "passthrough_to_default_spa" | "status_code_404"
    public string OnFailure { get; init; } = "status_code_502";
}
