using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

/// <summary>
/// 媒体流协同器：负责设备/会话作用域隔离、RFC 9110 后缀 Range 短探针免杀、以及媒体流寻轨抢占与去重。
/// </summary>
internal sealed class MediaStreamCoordinator
{
    private readonly ConcurrentDictionary<string, TunnelRequestSession> _activeMediaStreams = new();

    /// <summary>
    /// 提取客户端/设备/会话作用域标识 (Session Scope)
    /// 支持 Plex, Emby/Jellyfin 及通用环境，保证多用户/多设备并发播放媒体互不抢占。
    /// 优先级: 客户端设备级标识 > 身份凭据 > 独立播放会话 > 外部访客 IP > default
    /// </summary>
    public static string ExtractSessionScope(IReadOnlyDictionary<string, string> headers, string targetUrl)
    {
        string? queryClient = null;
        string? queryToken = null;
        string? querySession = null;

        // 1. 尝试从 URL 查询参数解析 (如 Plex, Emby/Jellyfin, STRM)
        if (!string.IsNullOrEmpty(targetUrl))
        {
            try
            {
                var qIdx = targetUrl.IndexOf('?');
                if (qIdx >= 0 && qIdx < targetUrl.Length - 1)
                {
                    var qs = targetUrl[(qIdx + 1)..];
                    foreach (var part in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split('=', 2);
                        var k = kv[0].Trim();
                        var v = kv.Length > 1 ? kv[1].Trim() : string.Empty;
                        if (string.IsNullOrEmpty(v)) continue;

                        if (k.Equals("X-Plex-Client-Identifier", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("DeviceId", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("deviceId", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("X-Emby-Device-Id", StringComparison.OrdinalIgnoreCase))
                        {
                            queryClient = v;
                        }
                        else if (k.Equals("X-Plex-Token", StringComparison.OrdinalIgnoreCase) ||
                                 k.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase) ||
                                 k.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                                 k.Equals("token", StringComparison.OrdinalIgnoreCase))
                        {
                            queryToken = v;
                        }
                        else if (k.Equals("X-Plex-Session-Id", StringComparison.OrdinalIgnoreCase) ||
                                 k.Equals("playSessionId", StringComparison.OrdinalIgnoreCase))
                        {
                            querySession = v;
                        }
                    }
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(queryClient)) return $"client:{queryClient}";

        // 2. 标头优先级提取
        string? GetHeader(string name)
        {
            foreach (var (k, v) in headers)
            {
                if (k.Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        var clientId = GetHeader("x-plex-client-identifier") ?? GetHeader("x-emby-device-id") ?? GetHeader("deviceid");
        if (!string.IsNullOrWhiteSpace(clientId)) return $"client:{clientId.Trim()}";

        if (!string.IsNullOrEmpty(queryToken)) return $"token:{queryToken}";

        var token = GetHeader("x-plex-token") ?? GetHeader("x-emby-token");
        if (!string.IsNullOrWhiteSpace(token)) return $"token:{token.Trim()}";

        if (!string.IsNullOrEmpty(querySession)) return $"session:{querySession}";

        var sessionId = GetHeader("x-plex-session-id") ?? GetHeader("play-session-id");
        if (!string.IsNullOrWhiteSpace(sessionId)) return $"session:{sessionId.Trim()}";

        var embyAuth = GetHeader("x-emby-authorization") ?? GetHeader("authorization");
        if (!string.IsNullOrWhiteSpace(embyAuth))
        {
            var matchId = Regex.Match(embyAuth, @"DeviceId=""?([^"",]+)""?", RegexOptions.IgnoreCase);
            if (matchId.Success && !string.IsNullOrWhiteSpace(matchId.Groups[1].Value)) return $"client:{matchId.Groups[1].Value}";

            var matchToken = Regex.Match(embyAuth, @"Token=""?([^"",]+)""?", RegexOptions.IgnoreCase);
            if (matchToken.Success && !string.IsNullOrWhiteSpace(matchToken.Groups[1].Value)) return $"token:{matchToken.Groups[1].Value}";
        }

        var clientIp = GetHeader("cf-connecting-ip") ?? GetHeader("x-real-ip") ?? GetHeader("x-forwarded-for");
        if (!string.IsNullOrWhiteSpace(clientIp)) return $"ip:{clientIp.Trim()}";

        return "default";
    }

    /// <summary>
    /// 识别是否为流媒体资源
    /// </summary>
    public static bool IsMediaResource(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.ToLowerInvariant();
        return p.Contains("/library/parts/") ||
               p.Contains("/videos/") ||
               p.Contains("/audio/") ||
               p.Contains("/strm/") ||
               p.EndsWith(".mp4") ||
               p.EndsWith(".mkv") ||
               p.EndsWith(".m4v") ||
               p.EndsWith(".ts") ||
               p.EndsWith(".flv") ||
               p.EndsWith(".avi") ||
               p.EndsWith(".mov") ||
               p.EndsWith(".webm") ||
               p.EndsWith(".mp3") ||
               p.EndsWith(".flac");
    }

    /// <summary>
    /// 构建规范化媒体键（剥离易变的临时会话查询参数）
    /// </summary>
    public static string GetCanonicalMediaKey(string sessionScope, string path)
    {
        var cleanPath = path;
        var qIdx = cleanPath.IndexOf('?');
        if (qIdx >= 0)
        {
            cleanPath = cleanPath[..qIdx];
        }
        return $"{sessionScope}:{cleanPath}";
    }

    /// <summary>
    /// 判定是否属于 RFC 9110 短探针切片（如后缀切片 bytes=-524288 或区间小于 2MB），此类请求绝不误杀主视频流。
    /// </summary>
    public static bool IsBoundedRangeProbe(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return false;

        var m = Regex.Match(rangeHeader, @"bytes=(\d+)?-(\d+)?", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        // 1. 后缀区间切片 (如 bytes=-524288，用于读取 MP4 moov 索引尾部)
        if (!m.Groups[1].Success && m.Groups[2].Success)
        {
            if (long.TryParse(m.Groups[2].Value, out var suffix) && suffix <= 2 * 1024 * 1024)
            {
                return true;
            }
        }

        // 2. 闭合有限小区间切片 (如 bytes=0-1048576)
        if (m.Groups[1].Success && m.Groups[2].Success)
        {
            if (long.TryParse(m.Groups[1].Value, out var start) && long.TryParse(m.Groups[2].Value, out var end))
            {
                if (end >= start && (end - start) <= 2 * 1024 * 1024)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 协同媒体流：若发现同设备同资源的旧主拉流（用户拖拽寻轨），优雅抢占并中止旧拉流，独占家庭上行带宽。
    /// </summary>
    public (TunnelRequestSession? SupersededOldSession, bool WasPreempted) CoordinateStream(string canonicalKey, TunnelRequestSession newSession, bool isProbe)
    {
        if (isProbe) return (null, false);

        TunnelRequestSession? superseded = null;
        _activeMediaStreams.AddOrUpdate(
            canonicalKey,
            newSession,
            (key, oldSession) =>
            {
                if (oldSession != null && !ReferenceEquals(oldSession, newSession) && oldSession.State != TunnelRequestState.Completed && oldSession.State != TunnelRequestState.Aborted)
                {
                    superseded = oldSession;
                }
                return newSession;
            });

        if (superseded != null)
        {
            superseded.Abort();
            return (superseded, true);
        }

        return (null, false);
    }

    public void Unregister(string canonicalKey, TunnelRequestSession session)
    {
        _activeMediaStreams.TryRemove(new KeyValuePair<string, TunnelRequestSession>(canonicalKey, session));
    }

    private readonly ConcurrentDictionary<string, (string ResolvedUrl, DateTime ExpiresAt)> _redirectCache = new();
    private const int MaxRedirectCacheSize = 2000;

    /// <summary>
    /// 缓存 302 重定向私网直链（如 STRM 局域网服务加速），跳过后续重复 302 多跳握手
    /// </summary>
    public void CacheRedirect(string canonicalKey, string resolvedUrl, TimeSpan? ttl = null)
    {
        var now = DateTime.UtcNow;
        if (_redirectCache.Count >= MaxRedirectCacheSize)
        {
            foreach (var (k, v) in _redirectCache)
            {
                if (now > v.ExpiresAt)
                {
                    _redirectCache.TryRemove(k, out _);
                }
            }
        }

        var expiresAt = now.Add(ttl ?? TimeSpan.FromMinutes(30));
        _redirectCache[canonicalKey] = (resolvedUrl, expiresAt);
    }

    /// <summary>
    /// 获取已缓存的 302 重定向私网直链
    /// </summary>
    public string? GetCachedRedirect(string canonicalKey)
    {
        if (_redirectCache.TryGetValue(canonicalKey, out var entry))
        {
            if (DateTime.UtcNow <= entry.ExpiresAt)
            {
                return entry.ResolvedUrl;
            }
            _redirectCache.TryRemove(canonicalKey, out _);
        }
        return null;
    }

    /// <summary>
    /// 使重定向直链缓存失效（如遇到 401/403/404 时主动自愈）
    /// </summary>
    public void InvalidateRedirect(string canonicalKey)
    {
        _redirectCache.TryRemove(canonicalKey, out _);
    }
}
