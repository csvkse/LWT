using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class TerminalDirectoryReportTests
{
    [Fact]
    public void Directory_report_handles_split_sequences_and_encoded_paths()
    {
        var parser = new TerminalDirectoryReportParser();
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "中文 space # ?"));
        var uri = new Uri(path).AbsoluteUri;
        Assert.Null(parser.Feed("prompt\x1b]7;" + uri[..8]));
        Assert.Null(parser.Feed(uri[8..] + "\x07"));
        Assert.Equal(path, parser.Feed("\x1b]133;A\x07"));
    }

    [Theory]
    [InlineData("\x1b]7;file://remote.invalid/tmp\x07")]
    [InlineData("\x1b]7;https://localhost/tmp\x07")]
    [InlineData("\x1b]7;file:///tmp/%0aunsafe\x07")]
    public void Directory_report_rejects_remote_or_invalid_paths(string input)
    {
        Assert.Null(new TerminalDirectoryReportParser().Feed(input));
    }
}
