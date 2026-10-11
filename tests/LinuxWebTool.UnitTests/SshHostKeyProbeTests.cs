using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class SshHostKeyProbeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host with spaces")]
    [InlineData("host;rm -rf /")]
    [InlineData("host&calc")]
    [InlineData("host|echo")]
    [InlineData("user@host")]
    [InlineData("host/path")]
    public async Task ProbeAsync_RejectsInvalidHostnames(string invalidHost)
    {
        var probe = new SshHostKeyProbe(NullLogger<SshHostKeyProbe>.Instance);
        var result = await probe.ProbeAsync(invalidHost, 22);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("无效的主机名", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task ProbeAsync_RejectsInvalidPorts(int invalidPort)
    {
        var probe = new SshHostKeyProbe(NullLogger<SshHostKeyProbe>.Instance);
        var result = await probe.ProbeAsync("127.0.0.1", invalidPort);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("端口必须在 1–65535 之间", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAsync_HandlesUnreachableHostGracefully()
    {
        var probe = new SshHostKeyProbe(NullLogger<SshHostKeyProbe>.Instance);
        // Connect to a local port that is unlikely to be listening to SSH
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await probe.ProbeAsync("127.0.0.1", 59999, cts.Token);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void SaveRcloneMountRequest_SupportsPrivateKeyContent()
    {
        const string mockPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nMOCK_KEY_DATA\n-----END OPENSSH PRIVATE KEY-----";
        var request = new SaveRcloneMountRequest
        {
            Name = "My SSH Server",
            Kind = "sftp",
            LocalPath = "/mnt/remote-ssh",
            Host = "192.168.1.100",
            Port = 22,
            Username = "root",
            HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl",
            PrivateKeyContent = mockPrivateKey,
            AutoMount = true,
            Enabled = true,
        };

        Assert.Equal("sftp", request.Kind);
        Assert.Equal(mockPrivateKey, request.PrivateKeyContent);
        Assert.Null(request.Password);
        Assert.Null(request.KeyFile);
    }
}
