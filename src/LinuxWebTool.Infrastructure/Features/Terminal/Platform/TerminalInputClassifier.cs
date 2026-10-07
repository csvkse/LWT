namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

internal static class TerminalInputClassifier
{
    public static bool IsProtocolReply(ReadOnlySpan<byte> input)
    {
        // xterm sends focus notifications and device/cursor reports through onData too.
        if (input.Length < 3 || input[0] != 27 || input[1] != '[') return false;
        if (input.Length == 3 && input[2] is (byte)'I' or (byte)'O') return true;
        if (input[^1] is not ((byte)'R' or (byte)'c' or (byte)'n' or (byte)'t')) return false;
        var body = input[2..^1];
        if (body.IsEmpty) return false;
        if (body[0] is (byte)'?' or (byte)'>' or (byte)'=') body = body[1..];
        if (body.IsEmpty) return false;
        foreach (var value in body)
            if (value != ';' && (value < '0' || value > '9')) return false;
        return true;
    }
}
