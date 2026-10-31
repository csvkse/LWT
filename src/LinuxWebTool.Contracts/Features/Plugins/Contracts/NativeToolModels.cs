namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

/// <summary>
/// 原生程序参数绑定令牌。
/// </summary>
public sealed record CommandArgumentBinding
{
    /// <summary>
    /// 绑定类型：literal, inputScalar, inputPath, artifactPath。
    /// </summary>
    public string Type { get; init; } = "literal";

    /// <summary>
    /// 字面量值（literal）或输入字段名（inputScalar/inputPath/artifactPath）。
    /// </summary>
    public string Value { get; init; } = "";

    /// <summary>
    /// 前缀（如 "-i=", "--output="）。
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// 条件字段名：若该字段为 false 或空，则忽略此绑定。
    /// </summary>
    public string? ConditionField { get; init; }
}

/// <summary>
/// 标准输入绑定配置。
/// </summary>
public sealed record CommandStdinBinding
{
    /// <summary>
    /// closed, text, json, file
    /// </summary>
    public string Mode { get; init; } = "closed";

    /// <summary>
    /// 来源字段名（text/json 模式下取输入字段，file 模式下取文件字段）。
    /// </summary>
    public string? SourceField { get; init; }
}

/// <summary>
/// 产物规则定义。
/// </summary>
public sealed record CommandArtifactRule
{
    public required string ArtifactId { get; init; }
    public required string RelativePath { get; init; }
    public string? ContentType { get; init; }
    public bool Optional { get; init; }
}

/// <summary>
/// 原生常驻管理接口配置 (HTTP 控制协议)。
/// </summary>
public sealed record ResidentControlSpec
{
    public int? Port { get; init; }
    public string? Token { get; init; }
    public string? BaseUrl { get; init; }
    public string HandshakePath { get; init; } = "/control/handshake";
    public string HealthPath { get; init; } = "/control/health";
    public string InvokePath { get; init; } = "/control/invoke";
    public string ShutdownPath { get; init; } = "/control/shutdown";
    public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];
}

/// <summary>
/// 一次性原生命令定义。
/// </summary>
public sealed record NativeCommandDefinition
{
    public required string CommandId { get; init; }
    public string ProgramRef { get; init; } = "";
    public System.Text.Json.JsonElement? InputSchema { get; init; }
    public IReadOnlyList<CommandArgumentBinding> ArgumentBindings { get; init; } = [];
    public CommandStdinBinding? StdinBinding { get; init; }
    public IReadOnlyList<int> AcceptedExitCodes { get; init; } = [0];
    public string ResultParser { get; init; } = "bounded-text"; // bounded-text, json, artifacts
    public string ProgressParser { get; init; } = "none"; // none, json-lines, ffmpeg-key-value
    public IReadOnlyList<CommandArtifactRule> ArtifactRules { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 30;
    public bool Mutates { get; init; } = false;
}

/// <summary>
/// 宿主解析并核验后的受信原生程序运行规格。
/// </summary>
public sealed record NativeProgramSpec
{
    /// <summary>
    /// 程序来源："package" (包内批准目录) | "system" (管理员登记的系统路径)
    /// </summary>
    public string Source { get; init; } = "package";
    public string ProgramRef { get; init; } = "";
    public string PackageGeneration { get; init; } = "1";
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> ArgumentList { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
    public string StdinMode { get; init; } = "closed";
    public string TargetPlatform { get; init; } = "linux";
    public string? StdinPayload { get; init; }
    public string? StdinFilePath { get; init; }
}
