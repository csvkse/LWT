using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Features.Security.Policies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class SecurityPolicyEngineTests
{
    [Theory]
    [InlineData("echo `whoami`", true)]
    [InlineData("docker run $(cat secret)", true)]
    [InlineData("echo \"test `id`\"", true)]
    [InlineData("echo 'no $(subshell) here'", false)] // 单引号内字面量
    [InlineData("echo \"just normal string\"", false)]
    [InlineData("docker ps -a", false)]
    [InlineData("echo \\`escaped\\`", false)]
    [InlineData("echo \\$(escaped)", false)]
    public void ShellCommandLexer_DetectsSubshellInjection(string cmd, bool expectedHasInjection)
    {
        var hasInjection = ShellCommandLexer.HasSubshellInjection(cmd.AsSpan());
        Assert.Equal(expectedHasInjection, hasInjection);
    }

    [Theory]
    [InlineData("docker ps; rm -rf /", 2)]
    [InlineData("echo a && echo b || echo c", 3)]
    [InlineData("cat file | grep secret", 2)]
    [InlineData("echo 'hello; world && test'", 1)] // 引号内的分隔符不切分
    [InlineData("docker run ubuntu bash", 1)]
    public void ShellCommandLexer_SplitsCommandSegments(string cmd, int expectedSegmentsCount)
    {
        var segments = ShellCommandLexer.SplitCommandSegments(cmd);
        Assert.Equal(expectedSegmentsCount, segments.Count);
    }

    [Theory]
    [InlineData("docker ps -a", "docker", "ps")]
    [InlineData("git status", "git", "status")]
    [InlineData("systemctl restart nginx", "systemctl", "restart")]
    [InlineData("/usr/bin/docker compose up -d", "docker", "compose")]
    [InlineData("sudo docker logs app", "docker", "logs")]
    [InlineData("ls -la", "ls", "")]
    public void ShellCommandLexer_ExtractsBinaryAndSubCommand(string cmd, string expectedBin, string expectedSub)
    {
        var (bin, sub) = ShellCommandLexer.ExtractBinaryAndSubCommand(cmd);
        Assert.Equal(expectedBin, bin);
        Assert.Equal(expectedSub, sub);
    }

    [Theory]
    [InlineData("/var/www/html/../", "/var/www")]
    [InlineData("//var///log//", "/var/log")]
    [InlineData("/./a/b/c", "/a/b/c")]
    [InlineData("/home/user/../../etc/passwd", "/etc/passwd")]
    [InlineData("", "/")]
    [InlineData("~", "/")]
    public void PosixPathNormalizer_NormalizesCorrectly(string input, string expected)
    {
        var result = PosixPathNormalizer.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/var/log/nginx/access.log", "/var/log", true)]
    [InlineData("/var/log", "/var/log", true)]
    [InlineData("/var/logging/test", "/var/log", false)] // 防止前缀匹配漏洞
    [InlineData("/etc/shadow", "/var/log", false)]
    [InlineData("/var/www", "*", true)]
    public void PosixPathNormalizer_IsWithin_EnforcesBoundary(string path, string allowedRoot, bool expected)
    {
        var result = PosixPathNormalizer.IsWithin(path, allowedRoot);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SecurityPolicyEngine_Simulate_EnforcesCommandWhitelist()
    {
        var rules = new List<SecurityPolicyRule>
        {
            new(
                RuleId: "r1",
                Name: "DockerReadOnly",
                Domain: SecurityTargetDomain.RemoteCommand,
                Effect: PolicyEffect.Allow,
                CommandPatterns: "docker ps; docker logs; docker inspect",
                PathPatterns: null,
                DisallowSubshells: true,
                DisallowCommandChaining: true,
                BlockHighRiskFlags: true,
                ReadOnlyOnly: false
            )
        };

        var engine = new SecurityPolicyEngine(new DummyTemplateStore(rules), NullLogger<SecurityPolicyEngine>.Instance);

        // 1. 允许白名单内指令
        var evalAllowed = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "docker ps -a",
            ClientIp: "127.0.0.1"
        );
        var decisionAllowed = engine.Simulate(rules, evalAllowed);
        Assert.True(decisionAllowed.IsAllowed);

        // 2. 拒绝白名单外指令
        var evalDenied = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "rm -rf /",
            ClientIp: "127.0.0.1"
        );
        var decisionDenied = engine.Simulate(rules, evalDenied);
        Assert.False(decisionDenied.IsAllowed);
        Assert.Contains("不在允许执行的命令白名单中", decisionDenied.DeniedReason);

        // 3. 拒绝子 Shell 注入
        var evalInjection = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "docker logs `whoami`",
            ClientIp: "127.0.0.1"
        );
        var decisionInjection = engine.Simulate(rules, evalInjection);
        Assert.False(decisionInjection.IsAllowed);
        Assert.Contains("动态子 Shell 展开", decisionInjection.DeniedReason);

        // 4. 拒绝复合链式命令
        var evalChain = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "docker ps && docker logs app",
            ClientIp: "127.0.0.1"
        );
        var decisionChain = engine.Simulate(rules, evalChain);
        Assert.False(decisionChain.IsAllowed);
        Assert.Contains("严禁使用", decisionChain.DeniedReason);
    }

    [Fact]
    public void SecurityPolicyEngine_Simulate_BlocksHighRiskFlags()
    {
        var rules = new List<SecurityPolicyRule>
        {
            new(
                RuleId: "r2",
                Name: "ContainerIsolation",
                Domain: SecurityTargetDomain.RemoteCommand,
                Effect: PolicyEffect.Allow,
                CommandPatterns: "docker *",
                PathPatterns: null,
                DisallowSubshells: false,
                DisallowCommandChaining: false,
                BlockHighRiskFlags: true,
                ReadOnlyOnly: false
            )
        };

        var engine = new SecurityPolicyEngine(new DummyTemplateStore(rules), NullLogger<SecurityPolicyEngine>.Instance);

        var evalPrivileged = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "docker run --privileged ubuntu",
            ClientIp: "127.0.0.1"
        );
        var decision = engine.Simulate(rules, evalPrivileged);
        Assert.False(decision.IsAllowed);
        Assert.Contains("特权模式", decision.DeniedReason);

        var evalSocket = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteCommand,
            ActionVerb: "Execute",
            TargetResource: "host-1",
            RawCommandOrPath: "docker run -v /var/run/docker.sock:/var/run/docker.sock alpine",
            ClientIp: "127.0.0.1"
        );
        var decisionSocket = engine.Simulate(rules, evalSocket);
        Assert.False(decisionSocket.IsAllowed);
        Assert.Contains("Docker套接字", decisionSocket.DeniedReason);
    }

    [Fact]
    public void SecurityPolicyEngine_Simulate_EnforcesFileReadOnlyAndPathScope()
    {
        var rules = new List<SecurityPolicyRule>
        {
            new(
                RuleId: "r3",
                Name: "LogReadOnly",
                Domain: SecurityTargetDomain.RemoteFile,
                Effect: PolicyEffect.Allow,
                CommandPatterns: null,
                PathPatterns: "/var/log;/data/logs",
                DisallowSubshells: false,
                DisallowCommandChaining: false,
                BlockHighRiskFlags: false,
                ReadOnlyOnly: true
            )
        };

        var engine = new SecurityPolicyEngine(new DummyTemplateStore(rules), NullLogger<SecurityPolicyEngine>.Instance);

        // 1. 允许读取范围内的文件
        var evalRead = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteFile,
            ActionVerb: "Read",
            TargetResource: "host-1",
            RawCommandOrPath: "/var/log/syslog",
            ClientIp: "127.0.0.1"
        );
        var decisionRead = engine.Simulate(rules, evalRead);
        Assert.True(decisionRead.IsAllowed);

        // 2. 拒绝写入
        var evalWrite = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteFile,
            ActionVerb: "Write",
            TargetResource: "host-1",
            RawCommandOrPath: "/var/log/syslog",
            ClientIp: "127.0.0.1"
        );
        var decisionWrite = engine.Simulate(rules, evalWrite);
        Assert.False(decisionWrite.IsAllowed);
        Assert.Contains("只读防护模式", decisionWrite.DeniedReason);

        // 3. 拒绝范围外的路径
        var evalOutside = new SecurityEvaluationContext(
            ApiKeyId: "test-key",
            ApiKeyName: "TestKey",
            Domain: SecurityTargetDomain.RemoteFile,
            ActionVerb: "Read",
            TargetResource: "host-1",
            RawCommandOrPath: "/etc/passwd",
            ClientIp: "127.0.0.1"
        );
        var decisionOutside = engine.Simulate(rules, evalOutside);
        Assert.False(decisionOutside.IsAllowed);
        Assert.Contains("超出已授权的安全目录边界", decisionOutside.DeniedReason);
    }

    private sealed class DummyTemplateStore(IReadOnlyList<SecurityPolicyRule> rules) : ISecurityPolicyTemplateStore
    {
        public Task<IReadOnlyList<SecurityPolicyTemplateItemResponse>> GetAllAsync() => Task.FromResult<IReadOnlyList<SecurityPolicyTemplateItemResponse>>([]);
        public Task<SecurityPolicyTemplateItemResponse?> GetByIdAsync(string id) => Task.FromResult<SecurityPolicyTemplateItemResponse?>(null);
        public Task<string> CreateAsync(CreateSecurityPolicyTemplateRequest request) => Task.FromResult("tpl-1");
        public Task<bool> UpdateAsync(string id, UpdateSecurityPolicyTemplateRequest request) => Task.FromResult(true);
        public Task<(bool Success, string? Error)> DeleteAsync(string id) => Task.FromResult((true, (string?)null));
        public Task<IReadOnlyList<SecurityPolicyRule>> GetEffectiveRulesForApiKeyAsync(string apiKeyId) => Task.FromResult(rules);
    }
}
