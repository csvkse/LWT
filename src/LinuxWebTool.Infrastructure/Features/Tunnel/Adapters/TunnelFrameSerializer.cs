using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
namespace LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

/// <summary>
/// 隧道 WebSocket 协议帧序列化器。
/// 使用 Utf8JsonWriter 实现原生 AOT / 反射禁用安全（零反射、零 TypeInfoResolver 依赖、高性能）。
/// 内置 WHATWG Fetch ByteString 规范安全清洗，杜绝中文标头引发边缘网关崩溃。
/// </summary>
internal static class TunnelFrameSerializer
{
    /// <summary>
    /// 确保标头值严格符合 WHATWG Fetch ByteString (0x00 ~ 0xFF) 规范。
    /// 若包含大于 127 的 Unicode 字符（如中文文件名、中文重定向路径），自动执行 UTF-8 百分号编码，
    /// 防止 Cloudflare DO V8 引擎抛出 TypeError: Cannot convert argument to a ByteString 并挂起连接。
    /// </summary>
    public static string SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        var needsEscape = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 127)
            {
                needsEscape = true;
                break;
            }
        }
        if (!needsEscape) return value;

        var sb = new StringBuilder(value.Length + 16);
        var utf8Bytes = Encoding.UTF8.GetBytes(value);
        foreach (var b in utf8Bytes)
        {
            if (b > 127)
            {
                sb.Append('%');
                sb.Append(b.ToString("X2"));
            }
            else
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    public static byte[] SerializeHttpResponse(string requestId, int status, IEnumerable<KeyValuePair<string, string>>? headers, string bodyBase64)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "HTTP_RESPONSE");
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("status", status);
            writer.WriteStartObject("headers");
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    writer.WriteString(kv.Key, SanitizeHeaderValue(kv.Value));
                }
            }
            writer.WriteEndObject();
            writer.WriteString("body", bodyBase64);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static byte[] SerializeHttpResponseStart(string requestId, int status, IEnumerable<KeyValuePair<string, string>>? headers)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "HTTP_RESPONSE_START");
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("status", status);
            writer.WriteStartObject("headers");
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    writer.WriteString(kv.Key, SanitizeHeaderValue(kv.Value));
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static byte[] SerializeHttpResponseEnd(string requestId)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "HTTP_RESPONSE_END");
            writer.WriteString("requestId", requestId);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }
}
