using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Mount.Adapters;

/// <summary>
/// 远端 SSH 主机公钥与指纹探测器。
/// 优先使用纯 C# 原生 SSH 握手（ecdh-sha2-nistp256），跨平台无外部依赖；在特殊环境回退到 ssh-keyscan。
/// </summary>
public sealed class SshHostKeyProbe(ILogger<SshHostKeyProbe> logger)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<SshHostKeyProbeResult> ProbeAsync(string host, int port = 22, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255
            || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or ';' or '&' or '|'))
        {
            return new(false, null, null, null, "无效的主机名或 IP 地址");
        }

        if (port is < 1 or > 65535)
        {
            return new(false, null, null, null, "端口必须在 1–65535 之间");
        }

        var cleanHost = host.Trim();

        // 1. 优先尝试纯 C# 原生 SSH-2 握手
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);
            var result = await ProbeWithSocketAsync(cleanHost, port, timeoutCts.Token);
            if (result.Success) return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "原生 SSH 握手探测异常，尝试回退系统 ssh-keyscan：{Host}:{Port}", cleanHost, port);
        }

        // 2. 回退机制：若系统已安装 ssh-keyscan 则尝试执行
        var fallback = await TrySshKeyscanAsync(cleanHost, port, cancellationToken);
        if (fallback.Success) return fallback;

        return new(false, null, null, null, fallback.ErrorMessage ?? "无法连接目标 SSH 服务器或协商公钥");
    }

    private static async Task<SshHostKeyProbeResult> ProbeWithSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();

        // 接收服务端 Banner（可能有多行非 SSH 文本提示，RFC 4253 §4.2）
        var bannerBuffer = new byte[1024];
        var bannerBuilder = new StringBuilder();
        var bannerFound = false;
        while (!bannerFound)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (line is null) return new(false, null, null, null, "服务端过早关闭连接");
            if (line.StartsWith("SSH-2.0-", StringComparison.Ordinal) || line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
            {
                bannerFound = true;
            }
        }

        // 发送客户端 Banner
        var clientBanner = Encoding.UTF8.GetBytes("SSH-2.0-LinuxWebTool_1.0\r\n");
        await stream.WriteAsync(clientBanner, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // 读取服务端 KEXINIT 数据包
        var serverKexInit = await ReadPacketAsync(stream, cancellationToken);
        if (serverKexInit.Length == 0 || serverKexInit[0] != 20) // SSH_MSG_KEXINIT
            return new(false, null, null, null, "非法的 SSH KEXINIT 响应");

        // 发送客户端 KEXINIT 数据包（提议 ecdh-sha2-nistp256，所有现代 SSH 服务器均标准支持）
        var clientKexInit = BuildKexInitPacket();
        await SendPacketAsync(stream, clientKexInit, cancellationToken);

        // 准备客户端临时 ECDH 公钥（NIST P-256）
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ecParams = ecdh.ExportParameters(false);
        if (ecParams.Q.X is null || ecParams.Q.Y is null)
            return new(false, null, null, null, "本地椭圆曲线公钥生成失败");

        // SSH 椭圆曲线未压缩点格式：0x04 || X (32 bytes) || Y (32 bytes)
        var qc = new byte[65];
        qc[0] = 0x04;
        Buffer.BlockCopy(ecParams.Q.X, 0, qc, 1, 32);
        Buffer.BlockCopy(ecParams.Q.Y, 0, qc, 33, 32);

        // 发送 SSH_MSG_KEX_ECDH_INIT (30)
        using var initMs = new MemoryStream();
        initMs.WriteByte(30);
        WriteSshString(initMs, qc);
        await SendPacketAsync(stream, initMs.ToArray(), cancellationToken);

        // 读取 SSH_MSG_KEX_ECDH_REPLY (31)
        var replyPacket = await ReadPacketAsync(stream, cancellationToken);
        if (replyPacket.Length == 0 || replyPacket[0] != 31)
            return new(false, null, null, null, $"未收到预期的密钥交换应答（packet {replyPacket.FirstOrDefault()}）");

        // 解析 K_S（服务器主机公钥字符串）
        using var replyMs = new MemoryStream(replyPacket);
        replyMs.ReadByte(); // 跳过 31 (消息号)
        var ks = ReadSshString(replyMs);
        if (ks.Length < 16) return new(false, null, null, null, "服务端主机公钥格式不完整");

        using var ksMs = new MemoryStream(ks);
        var keyType = Encoding.UTF8.GetString(ReadSshString(ksMs));
        var base64Key = Convert.ToBase64String(ks);
        var sha256Hash = SHA256.HashData(ks);
        var fingerprint = "SHA256:" + Convert.ToBase64String(sha256Hash).TrimEnd('=');
        var hostKey = $"{keyType} {base64Key}";

        return new(true, keyType, hostKey, fingerprint, null);
    }

    private static async Task<SshHostKeyProbeResult> TrySshKeyscanAsync(string host, int port, CancellationToken cancellationToken)
    {
        var scanner = FindSshKeyscan();
        if (scanner is null) return new(false, null, null, null, "无法通过原生协议获取公钥，且系统未安装 ssh-keyscan");

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = scanner,
                    ArgumentList = { "-p", port.ToString(), "-T", "5", host },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#')) continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var keyType = parts[1];
                    var base64 = parts[2];
                    try
                    {
                        var keyBytes = Convert.FromBase64String(base64);
                        var fp = "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBytes)).TrimEnd('=');
                        return new(true, keyType, $"{keyType} {base64}", fp, null);
                    }
                    catch { }
                }
            }

            return new(false, null, null, null, string.IsNullOrWhiteSpace(stderr) ? "未扫描到主机公钥" : stderr.Trim());
        }
        catch (Exception ex)
        {
            return new(false, null, null, null, $"ssh-keyscan 执行失败：{ex.Message}");
        }
    }

    private static string? FindSshKeyscan()
    {
        var candidates = new[] { "/usr/bin/ssh-keyscan", "/bin/ssh-keyscan", "ssh-keyscan", "ssh-keyscan.exe" };
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    private static byte[] BuildKexInitPacket()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(20); // SSH_MSG_KEXINIT
        var cookie = new byte[16];
        RandomNumberGenerator.Fill(cookie);
        ms.Write(cookie);

        WriteSshString(ms, Encoding.UTF8.GetBytes("ecdh-sha2-nistp256,curve25519-sha256"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("ssh-ed25519,ecdsa-sha2-nistp256,rsa-sha2-512,rsa-sha2-256,ssh-rsa"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("aes128-ctr,aes256-ctr,aes128-gcm@openssh.com"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("aes128-ctr,aes256-ctr,aes128-gcm@openssh.com"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("hmac-sha2-256"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("hmac-sha2-256"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("none"));
        WriteSshString(ms, Encoding.UTF8.GetBytes("none"));
        WriteSshString(ms, Array.Empty<byte>());
        WriteSshString(ms, Array.Empty<byte>());
        ms.WriteByte(0); // first_kex_packet_follows = false
        ms.Write(new byte[4]); // 0 (reserved)
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lenBytes = new byte[4];
        await ReadExactAsync(stream, lenBytes, 0, 4, cancellationToken);
        var len = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
        if (len is < 1 or > 35000) throw new IOException($"非法的 SSH 包长度：{len}");

        var packet = new byte[len];
        await ReadExactAsync(stream, packet, 0, len, cancellationToken);
        var padLen = packet[0];
        if (padLen >= len) throw new IOException("非法的 SSH 填充长度");

        var payload = new byte[len - padLen - 1];
        Buffer.BlockCopy(packet, 1, payload, 0, payload.Length);
        return payload;
    }

    private static async Task SendPacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var padLen = 8 - ((payload.Length + 5) % 8);
        if (padLen < 4) padLen += 8;
        var packetLen = payload.Length + padLen + 1;
        var buf = new byte[4 + packetLen];
        buf[0] = (byte)(packetLen >> 24);
        buf[1] = (byte)(packetLen >> 16);
        buf[2] = (byte)(packetLen >> 8);
        buf[3] = (byte)packetLen;
        buf[4] = (byte)padLen;
        Buffer.BlockCopy(payload, 0, buf, 5, payload.Length);
        await stream.WriteAsync(buf, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void WriteSshString(Stream s, byte[] bytes)
    {
        s.WriteByte((byte)(bytes.Length >> 24));
        s.WriteByte((byte)(bytes.Length >> 16));
        s.WriteByte((byte)(bytes.Length >> 8));
        s.WriteByte((byte)bytes.Length);
        s.Write(bytes);
    }

    private static byte[] ReadSshString(Stream s)
    {
        var lenBytes = new byte[4];
        ReadExact(s, lenBytes, 0, 4);
        var len = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
        if (len is < 0 or > 16384) throw new IOException($"非法的 SSH 字符串长度：{len}");
        var buf = new byte[len];
        ReadExact(s, buf, 0, len);
        return buf;
    }

    private static void ReadExact(Stream s, byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            var read = s.Read(buf, offset, count);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int offset, int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var read = await s.ReadAsync(buf.AsMemory(offset, count), cancellationToken);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        var b = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(b.AsMemory(0, 1), cancellationToken);
            if (read <= 0) break;
            if (b[0] == '\n') break;
            if (b[0] != '\r') ms.WriteByte(b[0]);
            if (ms.Length > 2048) throw new IOException("SSH 协议行长度超出限制");
        }
        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
    }
}
