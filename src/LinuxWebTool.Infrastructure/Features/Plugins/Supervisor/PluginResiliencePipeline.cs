using System.Collections.Concurrent;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件统一弹性韧性流水线 (Plugin Resilience Pipeline via Polly v8)
/// 负责为插件动作调用 (ExecuteActionAsync) 与 HTTP 内部调用提供工业级防护：
/// - 超时控制 (TimeoutStrategy)：防止不可控脚本/死锁微服务挂起调用端；
/// - 指数退避重试 (RetryStrategy)：平滑吸收微服务瞬时抖动；
/// - 熔断保护 (CircuitBreakerStrategy)：连续异常时主动熔断，避免雪崩冲击宿主 CPU；
/// - 纯 AOT 友好：基于 Polly v8 全新零分配与静态架构。
/// </summary>
public sealed class PluginResiliencePipeline
{
    private static readonly ConcurrentDictionary<string, ResiliencePipeline> Pipelines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或构建指定插件的专用韧性流水线。
    /// </summary>
    /// <param name="pluginId">插件标识</param>
    /// <param name="timeout">单次调用超时时限（默认 30 秒）</param>
    /// <param name="maxRetries">瞬态重试次数（默认 2 次）</param>
    /// <param name="breakDuration">熔断隔离时长（默认 10 秒）</param>
    public static ResiliencePipeline GetOrCreate(
        string pluginId,
        TimeSpan? timeout = null,
        int maxRetries = 2,
        TimeSpan? breakDuration = null)
    {
        return Pipelines.GetOrAdd(pluginId, id =>
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            var effectiveBreakDuration = breakDuration ?? TimeSpan.FromSeconds(10);

            var builder = new ResiliencePipelineBuilder();

            // 1. 瞬态重试策略 (指数退避)
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<TimeoutRejectedException>()
                    .Handle<HttpRequestException>()
            });

            // 2. 主动断路器策略 (连续 3 次失败熔断 10 秒)
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 3,
                BreakDuration = effectiveBreakDuration,
                SamplingDuration = TimeSpan.FromSeconds(30)
            });

            // 3. 超时中断策略
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = effectiveTimeout
            });

            return builder.Build();
        });
    }

    /// <summary>
    /// 重置指定插件的弹性管道状态（如人工修复后重启微服务）。
    /// </summary>
    public static void Reset(string pluginId)
    {
        Pipelines.TryRemove(pluginId, out _);
    }
}
