using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.Infrastructure.Features.Security.Adapters;

/// <summary>
/// API Key 安全生成、哈希验证与并发缓存服务
/// </summary>
public class ApiKeyService(ApiKeyStore store, ILogger<ApiKeyService> logger)
{
    private const string KeyPrefixTag = "lwt_live_";
    private readonly ConcurrentDictionary<string, ApiKeyEntity> _cache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTouched = new();
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromMinutes(1);

    /// <summary>生成新 API Key 并写入持久化</summary>
    public async Task<(string rawKey, ApiKeyEntity entity)> CreateAsync(CreateApiKeyRequest request)
    {
        var randomBytes = RandomNumberGenerator.GetBytes(16);
        var randomHex = Convert.ToHexString(randomBytes).ToLowerInvariant();
        var rawKey = $"{KeyPrefixTag}{randomHex}";
        var keyPrefix = rawKey[..Math.Min(12, rawKey.Length)];
        var keyHash = ComputeHash(rawKey);

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "未命名密钥" : request.Name.Trim(),
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            IsEnabled = true,
            AllowApi = request.AllowApi,
            AllowMcp = request.AllowMcp,
            AllowTerminal = request.AllowTerminal,
            AllowSchedules = request.AllowSchedules,
            AllowFiles = request.AllowFiles,
            AllowTranscode = request.AllowTranscode,
            AllowGateway = request.AllowGateway,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        await store.InsertAsync(entity);
        _cache[keyHash] = entity;
        return (rawKey, entity);
    }

    /// <summary>验证 API Key 是否有效、启用且未过期</summary>
    public async Task<ApiKeyEntity?> ValidateAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;

        var keyHash = ComputeHash(rawKey.Trim());

        if (!_cache.TryGetValue(keyHash, out var entity))
        {
            entity = await store.GetByHashAsync(keyHash);
            if (entity != null)
            {
                _cache[keyHash] = entity;
            }
        }

        if (entity is null || !entity.IsEnabled) return null;

        if (entity.ExpiresAt.HasValue && entity.ExpiresAt.Value < DateTime.UtcNow)
        {
            return null;
        }

        return entity;
    }

    /// <summary>使特定或全部缓存失效</summary>
    public void InvalidateCache(string? keyHash = null)
    {
        if (keyHash is not null)
        {
            _cache.TryRemove(keyHash, out _);
        }
        else
        {
            _cache.Clear();
            _lastTouched.Clear();
        }
    }

    /// <summary>异步更新最后使用时间戳（附带 1 分钟防抖阈值，避免高频请求压垮 SQLite 写入锁）</summary>
    public void TouchLastUsed(string id)
    {
        var now = DateTime.UtcNow;
        if (_lastTouched.TryGetValue(id, out var last) && now - last < TouchThrottle)
        {
            return;
        }

        _lastTouched[id] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await store.TouchLastUsedAsync(id);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "更新 API Key 最后使用时间失败");
            }
        });
    }

    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
