using System.Text.Json;
using LinuxWebTool.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace LinuxWebTool.WebHost.Gateway;

/// <summary>
/// 基于 SQLite 数据库的动态 YARP L7 代理配置提供者（支持毫秒级热重载）
/// 借鉴于 ProxyYARP。
/// </summary>
public sealed class DatabaseProxyConfigProvider : IProxyConfigProvider
{
    private readonly GatewayStore _store;
    private readonly WebsiteAllowList _allowList;
    private readonly ILogger<DatabaseProxyConfigProvider> _logger;
    private volatile DatabaseProxyConfig _currentConfig;

    public DatabaseProxyConfigProvider(
        GatewayStore store,
        WebsiteAllowList allowList,
        ILogger<DatabaseProxyConfigProvider> logger)
    {
        _store = store;
        _allowList = allowList;
        _logger = logger;
        _currentConfig = LoadConfig();
    }

    public IProxyConfig GetConfig() => _currentConfig;

    public void Reload()
    {
        var old = _currentConfig;
        _currentConfig = LoadConfig();
        old.SignalChange();
    }

    private DatabaseProxyConfig LoadConfig()
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        try
        {
            var dbRoutes = _store.GetAllRoutesAsync().GetAwaiter().GetResult();
            var dbClusters = _store.GetAllClustersAsync().GetAwaiter().GetResult();
            var websites = _store.GetAllWebsitesAsync().GetAwaiter().GetResult();

            _allowList.Replace(websites);

            foreach (var r in dbRoutes.Where(r => r.IsEnabled))
            {
                var match = new RouteMatch
                {
                    Path = r.MatchPath,
                    Hosts = string.IsNullOrWhiteSpace(r.MatchHosts) ? null : r.MatchHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                };

                var meta = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(r.Metadata))
                {
                    try
                    {
                        var dict = JsonSerializer.Deserialize(r.Metadata, Composition.AppJsonSerializerContext.Default.DictionaryStringString);
                        if (dict != null)
                        {
                            foreach (var (k, v) in dict) meta[k] = v;
                        }
                    }
                    catch { }
                }

                routes.Add(new RouteConfig
                {
                    RouteId = r.RouteId,
                    ClusterId = r.ClusterId,
                    Match = match,
                    Order = r.OrderNum,
                    Metadata = meta
                });
            }

            foreach (var c in dbClusters)
            {
                var destinations = new Dictionary<string, DestinationConfig>();
                if (!string.IsNullOrWhiteSpace(c.Destinations))
                {
                    try
                    {
                        var destList = JsonSerializer.Deserialize(c.Destinations, Composition.AppJsonSerializerContext.Default.ListDestinationItem);
                        if (destList != null)
                        {
                            for (var i = 0; i < destList.Count; i++)
                            {
                                destinations[$"dest{i}"] = new DestinationConfig { Address = destList[i].Address };
                            }
                        }
                    }
                    catch { }
                }

                clusters.Add(new ClusterConfig
                {
                    ClusterId = c.ClusterId,
                    LoadBalancingPolicy = c.LoadBalancingPolicy,
                    Destinations = destinations
                });
            }

            // 自动注入网站代理 Catch-All 路由与集群（当存在已启用的代理网站时）
            if (websites.Any(w => w.IsEnabled))
            {
                var anyBodyRewrite = websites.Any(w => w.IsEnabled && w.RewriteBody);
                var anyCookieRewrite = websites.Any(w => w.IsEnabled && w.RewriteCookie);

                routes.Add(new RouteConfig
                {
                    RouteId = "__website_proxy_catchall",
                    ClusterId = "__website_proxy_cluster",
                    Match = new RouteMatch { Path = "/{**catchall}" },
                    Order = int.MaxValue,
                    Metadata = new Dictionary<string, string>
                    {
                        [WebsiteProxyTransformProvider.MetadataKey] = "true",
                        [WebsiteProxyTransformProvider.MetadataKey + ".AnyBodyRewrite"] = anyBodyRewrite ? "true" : "false",
                        [WebsiteProxyTransformProvider.MetadataKey + ".AnyCookieRewrite"] = anyCookieRewrite ? "true" : "false"
                    }
                });

                clusters.Add(new ClusterConfig
                {
                    ClusterId = "__website_proxy_cluster",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["placeholder"] = new DestinationConfig { Address = "http://localhost" }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load database proxy configuration.");
        }

        return new DatabaseProxyConfig(routes, clusters);
    }

    public sealed class DestinationItem
    {
        public string Address { get; set; } = string.Empty;
    }

    private sealed class DatabaseProxyConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public DatabaseProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IChangeToken ChangeToken { get; }

        public void SignalChange() => _cts.Cancel();
    }
}
