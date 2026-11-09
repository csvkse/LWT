# 借鉴 ProxyYARP 代理库与家庭网关融合技术方案

## 1. 核心结论与可行性判定

> **核心结论**：**不仅完全可行，而且是极其绝妙的高收益架构协同方案！**
> 
> 借鉴 `ProxyYARP`（`E:\WorkProject\C#\【个人项目】\命令行\ProxyYARP`）的代理库与架构，不仅能让 FRP 穿透客户端从「单机单服务穿透」直接升维为「全屋/全内网多服务一站式穿透」，还能让 `LinuxWebTool` 直接获得工业级 **L7 动态反向代理**、**即席网站代理（带 HTML/Cookie 改写）** 以及 **L4 TCP/UDP 高性能端口转发** 三大核心家庭网关能力。

### 1.1 技术栈 100% 同构与 Native AOT 已证可行

| 考量维度 | LinuxWebTool 当前架构 | ProxyYARP 既有架构 | 融合兼容性结论 |
|---|---|---|---|
| **目标框架** | `.NET 10` (`net10.0`) | `.NET 10` (`net10.0`) | **完全一致** |
| **Native AOT 编译** | `<IsAotCompatible>true</IsAotCompatible>` | `<PublishAot>true</PublishAot>` | **完全一致** |
| **持久化技术** | SQLite + Dapper.AOT | SQLite / PostgreSQL + Dapper.AOT | **完全一致** |
| **YARP AOT 兼容性** | 尚未引入 | 已实装 `Yarp.ReverseProxy 2.3.0` + `rd.xml` | **已在生产实测通过** |
| **前端技术** | Vue 3 + Tailwind CSS (原生 ES 模块) | Vue 3 + Tailwind CSS | **风格统一，UI 无缝迁移** |

---

## 2. 深度融合带来的两大跨越式价值

### 2.1 价值一：FRP 穿透引擎的降维升维 (FRP + YARP 管道融合)

#### 原方案痛点
原方案中，FRP 客户端收到 Cloudflare 边缘下发的 `HTTP_REQUEST` 后，仅能通过简单的 `HttpClient` 将流量转发至固定的本地端口（如 `http://127.0.0.1:8080`），即**一台机器只穿透自己**。若想访问内网其他机器（如 Home Assistant、软路由、Jellyfin），需要部署多个客户端或复杂端口映射。

#### 融合 ProxyYARP 后的高阶形态 (全屋穿透中枢)
FRP 客户端收到请求后，不直接绑死单机地址，而是接入 **YARP 内部处理管道**：

```
[ 公网访客 / AI 客户端 ]
          │
          ▼
[ Cloudflare 边缘网关 (frp.example.com) ]
          │  WebSocket 长连接 (HTTP-over-WebSocket)
          ▼
[ LinuxWebTool 内部 FRP 客户端 ]
          │  构造 HttpContext 并提交给 YARP 管道
          ▼
┌─────────────────────────────────────────────────────────────┐
│                 YARP 智能反代与网关引擎 (ProxyYARP)           │
│                                                             │
│  ├─ 路由 1: /                   ──▶ LinuxWebTool 本机控制台 │
│  ├─ 路由 2: /ha/*               ──▶ 192.168.1.10:8123 (HA)   │
│  ├─ 路由 3: /jellyfin/*         ──▶ 192.168.1.20:8096 (媒体) │
│  ├─ 路由 4: /ollama/*           ──▶ 192.168.1.50:11434 (AI)  │
│  └─ 网站代理: /http://192.168.1.1 ──▶ 路由器 LuCI 后台 (自动改写)│
└─────────────────────────────────────────────────────────────┘
```

**核心优势**：
1. **单一隧道穿透整网**：用户只需在 Cloudflare 维持一条长连接隧道（例如 `myhome`），即可经由路径路由或网站代理安全访问局域网所有物理设备与 Docker 容器。
2. **工业级流控与韧性**：复用 YARP 内部针对 HTTP/1.1、HTTP/2、Keep-Alive 优化成熟的连接池，解决手写 `HttpClient` 容易遭遇的套接字耗尽与流媒体缓冲卡顿问题。

---

### 2.2 价值二：LinuxWebTool 原生进化为「家庭智能网关 (Home Gateway)」

通过吸纳 ProxyYARP 的核心模块，LinuxWebTool 将具备完整的家庭/企业网关能力：

```
                              ┌───────────────────────────────┐
                              │      LinuxWebTool 宿主服务    │
                              └──────────────┬────────────────┘
                                             │
             ┌───────────────────────────────┼───────────────────────────────┐
             ▼                               ▼                               ▼
  【L7 动态 HTTP 反向代理】       【即席网站代理 (极客特性)】       【L4 TCP/UDP 端口转发】
  (YarpProxyModule)               (WebsiteProxyTransform)         (TcpProxyEngine / Udp)
  • 路由与集群负载均衡            • 零配置代理任意内网网页        • 非阻塞套接字 + ArrayPool
  • 毫秒级热重载 (无需重启)       • 绝对路径自动改写              • RDP 3389、SSH 22 转发
  • 主动/被动健康检查             • Cookie Path 隔离与 SSRF 防护  • 游戏服务器与数据库端口
```

#### 特别优势：即席网站代理 (Website Proxy)
ProxyYARP 中的 `WebsiteProxyTransformProvider` 彻底解决了内网网页反代的历史级顽疾：
1. **绝对路径改写 (Body Rewrite)**：将网页返回的 `<script src="/static/app.js">` 自动重写为带网关前缀的地址，防止页面静态资源 404；
2. **Cookie 作用域隔离 (Cookie Rewrite)**：将 `Set-Cookie: Path=/` 改写为该站点专属路径，防止不同设备后台同名 Cookie 互相覆盖串号；
3. **SSRF 安全白名单**：仅放行在网关显式登记过的内网 IP / 域名，严禁未授权扫描内网，杜绝安全风险。

---

## 3. 具体代码与模块借鉴方案

### 3.1 引入包与 AOT 根描述符配置

在 `LinuxWebTool.WebHost.csproj` 中引入已验证兼容的 YARP 依赖：

```xml
<ItemGroup>
  <PackageReference Include="Yarp.ReverseProxy" Version="2.3.0" />
</ItemGroup>

<PropertyGroup>
  <!-- 抑制 YARP 内部在 AOT 时的已知安全告警 -->
  <NoWarn>$(NoWarn);IL2026;IL3050</NoWarn>
</PropertyGroup>

<!-- 引入 ProxyYARP 验证过的 AOT 描述符 -->
<ItemGroup>
  <TrimmerRootDescriptor Include="rd.xml" />
</ItemGroup>
```

### 3.2 模块代码复用映射表

| ProxyYARP 原类文件 | LinuxWebTool 目标归属 | 职能说明 |
|---|---|---|
| `Proxy/Yarp/YarpProxyModule.cs` | `LinuxWebTool.Infrastructure/Gateway/YarpGatewayModule.cs` | YARP 管道与服务依赖注入组装 |
| `Proxy/Yarp/DatabaseProxyConfigProvider.cs` | `LinuxWebTool.Infrastructure/Gateway/DatabaseProxyConfigProvider.cs` | 读写 SQLite 路由与集群，毫秒级推送配置变更信号 |
| `Proxy/Yarp/WebsiteProxyTransformProvider.cs` | `LinuxWebTool.Infrastructure/Gateway/WebsiteProxyTransformProvider.cs` | 网站代理核心：HTML/CSS 路径改写、Cookie 隔离、重定向修正 |
| `Proxy/Tcp/TcpProxyEngine.cs` | `LinuxWebTool.Infrastructure/Gateway/TcpProxyEngine.cs` | 高性能四层 TCP 端口转发后台服务 |
| `Proxy/Udp/UdpProxyEngine.cs` | `LinuxWebTool.Infrastructure/Gateway/UdpProxyEngine.cs` | 高性能四层 UDP 端口转发后台服务 |
| `Api/RoutesApi.cs`、`Api/WebsitesApi.cs` | `LinuxWebTool.WebHost/Routes/GatewayController.cs` | 网关路由、网站代理、TCP 转发的 Minimal API 控制器 |

---

## 4. 数据库持久化结构扩展

在 `DbSetup.cs` 中增加网关相关数据表（与 ProxyYARP 完全兼容）：

```sql
-- 1. L7 反向代理路由表
CREATE TABLE IF NOT EXISTS gateway_route (
  Id TEXT PRIMARY KEY,
  RouteId TEXT NOT NULL UNIQUE,
  ClusterId TEXT NOT NULL,
  MatchPath TEXT NOT NULL,
  MatchHosts TEXT,
  Transforms TEXT,
  Metadata TEXT,
  OrderNum INTEGER NOT NULL DEFAULT 0,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL
);

-- 2. L7 反向代理目标集群表
CREATE TABLE IF NOT EXISTS gateway_cluster (
  Id TEXT PRIMARY KEY,
  ClusterId TEXT NOT NULL UNIQUE,
  LoadBalancingPolicy TEXT NOT NULL DEFAULT 'RoundRobin',
  Destinations TEXT NOT NULL, -- JSON: [{"Address": "http://192.168.1.10:8123"}]
  HealthCheckConfig TEXT,
  UpdateTime TEXT NOT NULL
);

-- 3. 网站代理登记表
CREATE TABLE IF NOT EXISTS gateway_website (
  Id TEXT PRIMARY KEY,
  Name TEXT NOT NULL,
  TargetUrl TEXT NOT NULL UNIQUE,
  RewriteBody INTEGER NOT NULL DEFAULT 1,
  RewriteCookie INTEGER NOT NULL DEFAULT 1,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL
);

-- 4. L4 四层端口转发规则表
CREATE TABLE IF NOT EXISTS gateway_tcp_route (
  Id TEXT PRIMARY KEY,
  Name TEXT NOT NULL,
  Protocol TEXT NOT NULL DEFAULT 'TCP', -- TCP | UDP
  ListenPort INTEGER NOT NULL UNIQUE,
  ForwardHost TEXT NOT NULL,
  ForwardPort INTEGER NOT NULL,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL
);
```

---

## 5. 与 APIKey 权限矩阵及 MCP 服务的深度协同

将网关功能纳入统一权限矩阵与智能体工具库：

### 5.1 APIKey 权限矩阵扩展
- 新增权限维度：`AllowGateway`（是否允许配置或经由网关访问内网设备）。
- 控制粒度：
  - 外部只读 Token：可被限定只能访问特定的内网反代路由，不能访问宿主机终端或文件系统；
  - 自动化运维 Token：具备 `AllowGateway` 权限，可通过 API 热更新内网转发端口。

### 5.2 赋能 MCP 智能体 (AI Agent Gateway Tools)
为 AI Agent 赋予内网设备洞察与路由调度能力：
- `gateway_list_services`: 查询当前家庭网关中已登记的内网服务与健康状态（如 HA、NAS、摄像头、软路由）。
- `gateway_add_website_proxy`: 允许 AI 临时为用户开通某台内网设备的外部代理访问链接。
- `gateway_get_device_status`: 检查指定内网设备在网关层的主动健康检测回包。

---

## 6. 前端 UI 整合规划

在 `LinuxWebTool` 导航栏中新增 **「家庭网关 (Gateway)」** 模块，采用 Tab 分页聚合 ProxyYARP 现有的成熟界面：

```
[ 家庭网关 (Gateway) ]
  ├─ [ Tab 1: HTTP 路由与集群 ] (L7 路径匹配、目标地址配置、权重与健康检查)
  ├─ [ Tab 2: 网站代理 (极客) ] (一键新增内网网站、开启/关闭绝对路径改写、点击直达)
  └─ [ Tab 3: TCP/UDP 端口转发 ] (四层监听端口、内网目标主机/端口、实时转发测试)
```

通过这一融合，用户只需运行一个体积不足 30MB 的 `LinuxWebTool` 原生单二进制程序，即可同时拥有 **Linux 主机运维 + FFmpeg 转码中枢 + FRP 反向穿透客户端 + 具备改写能力的家庭智能网关 + AI MCP 开放服务** 的五合一终极全能中枢！
