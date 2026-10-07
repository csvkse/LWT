namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

internal sealed class TerminalDirectoryReportParser
{
    private string pending = "";
    private string? pendingDirectory;
    public bool IsAtPrompt { get; private set; }
    public void ResetPrompt() { IsAtPrompt = false; pendingDirectory = null; pending = ""; }
    public string? Feed(string text)
    {
        pending += text;
        string? latest = null;
        while (true)
        {
            var start = pending.IndexOf("\x1b]", StringComparison.Ordinal);
            if (start < 0) { pending = pending[^Math.Min(1, pending.Length)..]; break; }
            pending = pending[start..];
            var bell = pending.IndexOf('\x07', 2);
            var st = pending.IndexOf("\x1b\\", 2, StringComparison.Ordinal);
            var end = bell < 0 ? st : st < 0 ? bell : Math.Min(bell, st);
            if (end < 0)
            {
                if (pending.Length > 8192) pending = "";
                break;
            }
            var content = pending[2..end];
            pending = pending[(end + (end == st ? 2 : 1))..];
            if (content == "133;A")
            {
                IsAtPrompt = true;
                latest = pendingDirectory;
                pendingDirectory = null;
                continue;
            }
            if (!content.StartsWith("7;", StringComparison.Ordinal)) continue;
            IsAtPrompt = false;
            pendingDirectory = null;
            var report = content[2..];
            if (report.Length > 4096 || !Uri.TryCreate(report, UriKind.Absolute, out var uri)
                || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !(uri.Host.Length == 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))) continue;
            var path = uri.LocalPath;
            if (path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) continue;
            pendingDirectory = path;
        }
        return latest;
    }
}
