# 12 种主流代理协议矩阵与进阶传输层深度调研与架构设计规范

> **文档标识**：`LWT-ARCH-FORWARDER-005`  
> **状态**：已评审 (Approved)  
> **核心主题**：12 种主流代理协议矩阵 (VMess, VLESS, Shadowsocks, Trojan, Hysteria2, WireGuard, MASQUE, SOCKS, HTTP, TUIC, Anytls, NaïveProxy)、传输层与安全策略解耦、uTLS 浏览器指纹拟态、QUIC 用户态性能优化、微内核注册表架构  
> **适用模块**：`plugins/socks5-forwarder` / LinuxWebTool 微服务平台  
> **核心模式**：微内核注册表 (Registry)、桥接模式 (Bridge)、策略模式 (Strategy)、适配器模式 (Adapter)、有限状态机 (FSM)  

---

## 1. 架构总览与全协议生态矩阵 (The Poly-Protocol Matrix)

现代网络基础设施面临着多样化的部署环境与复杂的抗封锁/穿透需求。将 `socks5-forwarder` 从单一的 WebSocket 隧道中继器升级为**具备 12 种主流协议解析与出站能力的工业级全能网关**，需要清晰梳理每种协议的物理原理、加密模型与适用场景：

```mermaid
mindmap
  root((SOCKS5 Forwarder 12 种主流协议生态))
    TCP 与 TLS 传统流式协议
      SOCKS4 / SOCKS5 (原生局域网与内网中继通用标准)
      HTTP / HTTPS CONNECT (企业防火墙与正向代理)
      Shadowsocks / SS-2022 (无状态 AEAD 高速加密)
      Trojan (HTTPS 标准 443 伪装与探测免疫)
    现代高性能无损与混淆协议
      VMess (V2Ray 传统 AEAD 鉴权多载荷)
      VLESS (零冗余加密 + XTLS 0-copy + Reality 借牌免域名)
      Anytls (四层协议通用 TLS 混淆包装管道)
      NaïveProxy (Chromium 网络栈同款 JA3/JA4 浏览器指纹)
    UDP 与 QUIC 现代低延迟协议
      Hysteria2 (定制 QUIC + BBRv2 拥塞控制 + Salamander 混淆 + 端口跳跃)
      TUIC v5 (原生 QUIC 0-RTT 首包极速握手)
      MASQUE (IETF RFC 9298/9484 CONNECT-UDP/IP 国际企业标准)
      WireGuard (Noise 框架轻量内核级点对点 VPN)
```

---

## 2. 12 种协议深度物理原理、可行性与性能瓶颈深度剖析

### 2.1 传统四层与流式协议组

#### 1. SOCKS (SOCKS4 / SOCKS4a / SOCKS5 / SOCKS5h)
- **协议物理原理**：RFC 1928 标准协议。通过协商认证（无认证或 RFC 1929 用户名密码），发送 CONNECT 请求传递目标地址（IPv4 / IPv6 / Domain）。
- **可行性与库选型**：**100% 原生自研**（出站与入站握手解析仅需 ~150 行代码）。Go 标准库子项目 `golang.org/x/net/proxy` 提供开箱即用的 Client Dialer。
- **优缺点与边界**：协议明文无加密，绝对禁止在不可信公网传输；但在内网中转、局域网穿透中性能极高，CPU 消耗为 0。

#### 2. HTTP (HTTP/1.1 CONNECT, HTTP/2 CONNECT, HTTPS)
- **协议物理原理**：向代理服务器发送 `CONNECT host:port HTTP/1.1`，带可选 `Proxy-Authorization: Basic ...` 鉴权，代理握手成功返回 `200 Connection Established` 后建立透明双工管道。
- **可行性与库选型**：**100% 原生自研**（~40 行代码）或复用 `net/http` 及 `golang.org/x/net/http2`。
- **优缺点与边界**：兼容性极佳，几乎所有企业中间件与防毒墙均原生支持；明文 HTTP CONNECT 易暴露目标域名，建议套用 HTTPS (TLS)。

#### 3. Shadowsocks (SS-2022 & 传统 AEAD)
- **协议物理原理**：
  - 传统 AEAD（`aes-128-gcm`, `chacha20-ietf-poly1305`）：利用派生子密钥（Salt + Key ➔ Subkey）对每个数据块进行独立认证加密；
  - SS-2022 最新标准：强制固定长度握手头、抗主动重放校验缓存（Replay Filter）与 BLAKE2b/BLAKE3 密钥派生，彻底免疫主动嗅探探测。
- **可行性与库选型**：推荐集成 **`github.com/shadowsocks/go-shadowsocks2`** 或 **`github.com/sagernet/sing-shadowsocks`**。纯 Go 实现，无 CGO 依赖。
- **性能优势**：对称加密运算极快，建连开销仅 1 RTT，内存开销 < 500KB。

#### 4. Trojan
- **协议物理原理**：客户端与服务端建立标准 TLS 1.3 握手，随后首个应用层数据包直接发送：
  $$\text{Payload} = \text{Hex}(\text{SHA224}(\text{Password})) + \text{"\r\n"} + \text{SOCKS5\_CMD} + \text{ATYP} + \text{DST\_ADDR} + \text{DST\_PORT} + \text{"\r\n"}$$
  服务端若校验失败，则自动反代至合规 Web 站点（如 Nginx/Caddy），杜绝泄露代理指纹。
- **可行性与库选型**：**100% 原生 Go 构建**。利用 `crypto/tls` + 首包序列化仅需 ~100 行代码。
- **优势**：外表完全等同于普通 HTTPS 访问，抗主动探测能力极高。

---

### 2.2 现代进阶混淆与拟态协议组

#### 5. VMess
- **协议物理原理**：V2Ray 经典协议。由 16 字节认证信息、动态指令集请求头（HMAC-SHA256 / FNV1a）和数据载荷组成，全面推行 VMessAEAD 校验。
- **底层传输搭配**：支持 TCP、WebSocket（WS）、gRPC、HTTP/2 及 TLS。
- **瓶颈头脑风暴**：
  - **双重加密 CPU 膨胀**：VMess 内部已经包含一层 AEAD 加密，若外层再次套用 TLS/HTTPS，会导致 CPU 进行两次计算，千兆大带宽吞吐下 CPU 负载过高；
  - **选型建议**：推荐集成 `github.com/sagernet/sing-vmess`，主要用于兼容用户已有的历史 V2Ray 节点。

#### 6. VLESS (含 XTLS Vision 与 Reality 借牌机制)
- **协议物理原理**：
  - **Zero-Encryption 设计**：VLESS 本身彻底抛弃对称加密，协议头极轻（版本 + UUID + 附加指令 + 目标），将数据加密全部移交给外层 TLS 1.3；
  - **Reality 借牌技术**：客户端不需要自己购买域名和申请证书，直接在 TLS ClientHello 中使用权威公网站点（如 `www.apple.com`、`www.microsoft.com`）的 SNI，并结合 X25519 临时密钥与预共享私钥完成身份校验。主动扫描器使用任何证书去探测服务端，均只会得到真实的苹果或微软证书，**彻底解决证书指纹与域名关联封锁难题**！
- **可行性与库选型**：纯 Go VLESS 协议栈仅需 ~200 行；Reality 鉴权需引入 **`github.com/refraction-networking/utls`**。
- **性能评估**：由于消除了内层重复加密，且支持流控直通，吞吐量相比 VMess 提升 **40%~60%**。

#### 7. Anytls (AnyTLS 任意协议 TLS 混淆包装管道)
- **协议物理原理**：将任意无加密或弱特征四层数据流（如裸 TCP、SOCKS5、轻量中继流）在外层透明封装入 TLS 1.3 管道，配置动态 ALPN 协商（`h2`, `http/1.1`）与 SNI 伪装，并填充随机 Padding 数据包长度，使任何审查设备只能判定其为合规 HTTPS 流量。
- **可行性**：纯 Go 标准库 `crypto/tls` 即可无缝实现。

#### 8. NaïveProxy (Chromium 网络栈指纹深度拟态)
- **致命痛点背景**：常规 Go 语言 `crypto/tls` 在发送 ClientHello 时，其密码套件列表、扩展字段排列顺序、椭圆曲线参数等（即 **JA3 / JA4 指纹**）带有极其明显的 Go 标准库特征，易被深度学习检测算法归类为“非浏览器脚本流量”。
- **纯 Go 零 CGO 实现突破**：
  - 原版 NaïveProxy 依赖庞大的 C++ Chromium Net Stack，难以直接集成；
  - **工业级替代路线**：采用 **`github.com/refraction-networking/utls`** 拟态 Chrome 官方指纹（`utls.HelloChrome_Auto`），结合 `golang.org/x/net/http2` 发送合规带有 Basic 鉴权的 HTTP/2 CONNECT 报文。
  - **效果**：在 TLS 指纹、ALPN、HTTP/2 SETTINGS 帧行为上与真正的 Chrome 浏览器达到 **99.9% 像素级一致**，免疫高级机器学习指纹封锁。

---

### 2.3 UDP 与 QUIC 现代低延迟协议组

#### 9. Hysteria2 (Hy2)
- **协议物理原理**：
  - 基于深度重构的 **QUIC 协议 (UDP)**；
  - **Salamander 混淆**：使用预共享密钥对整个 UDP 数据包进行流密码混淆，抹杀标准 QUIC 数据包特征，防止被针对性阻断；
  - **BBRv2 / Brutal 拥塞控制**：针对高丢包跨洋网络优化，即使在 15%~30% 恶劣丢包环境下依然能强制跑满设定带宽；
  - **端口跳跃 (Port Hopping)**：客户端在多个服务端监听端口之间动态切换，抵御单个 UDP 端口被 ISP 动态封锁或惩罚性限速。
- **可行性与库选型**：直接集成官方核心模块 **`github.com/apernet/hysteria/core/v2`**。
- **性能与瓶颈攻防**：
  - *瓶颈*：高 PPS（每秒数据包数）会导致大量的系统调用与软中断。
  - *对策*：在 Linux 容器中必须启用 UDP GSO/GRO（Generic Segmentation/Receive Offload）批处理优化，吞吐可提升 3 倍。

#### 10. TUIC (TUIC v5)
- **协议物理原理**：专为跨洋低时延代理设计的 **原生 QUIC 协议**。
- **核心王牌特性**：
  - **真正的 0-RTT 首包握手**：借助 QUIC 会话复用 Ticket，首个客户端 UDP 数据包即可同时携带鉴权令牌、目标地址与应用层有效载荷，握手延迟直接降为 **0 毫秒**！
  - **UDP 转发模式优化**：支持 QUIC 原生 Datagram 传输与单流多路复用，避免队头阻塞。
- **可行性与库选型**：推荐集成 **`github.com/sagernet/sing-quic`** 或参考 `github.com/EAimTY/tuic` 纯 Go 规范。

#### 11. MASQUE (IETF RFC 9298 / RFC 9484 企业级标准)
- **协议物理原理**：
  - 苹果 iCloud 专用代理（Private Relay）与 Cloudflare WARP/Zero Trust 所采用的官方国际标准；
  - 基于 **HTTP/3 (QUIC)** 扩展出 `CONNECT-UDP`（RFC 9298）与 `CONNECT-IP`（RFC 9484）；
  - 将 UDP 数据报作为 HTTP/3 Datagram（Capsule 封装）安全传输。
- **可行性与库选型**：基于 **`github.com/quic-go/quic-go`** 与 **`github.com/quic-go/http3`** 构建。合规性与企业级演进潜力最强。

#### 12. WireGuard
- **协议物理原理**：现代内核级点对点 VPN 协议，基于 Noise 协议框架（ChaCha20-Poly1305, Curve25519, BLAKE2s），固定包长无指纹。
- **可行性与库选型**：
  - 纯用户态 Go 实现：**`golang.zx2c4.com/wireguard/wireguard-go`**；
  - 结合 LWT 宿主已验证的 `/dev/net/tun` 驱动，或者直接将 WireGuard 流量在用户态桥接给 `gVisor netstack`，实现无需配置宿主路由的“纯应用级用户态 WireGuard 出站”！

---

## 3. 12 种协议全景技术评估与横向对比矩阵

| 序号 | 协议名称 | 传输层基石 | 核心安全与加密机制 | 抗审查 / DPI 伪装等级 | 建连延迟 (RTT) | 内存占用预估 | 吞吐与 CPU 开销 | 推荐 Go 依赖库 |
| :---: | :--- | :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **1** | **VMess** | TCP / WS / gRPC | UUID + VMessAEAD | ⭐⭐⭐ | 2~3 RTT | ~2.5 MB | 中等 (双重加密耗 CPU) | `sagernet/sing-vmess` |
| **2** | **VLESS** | TCP / WS / gRPC | 外层 TLS / Reality (内层 0 加密) | ⭐⭐⭐⭐⭐ | 1~2 RTT (Reality 1-RTT) | ~1.5 MB | **极低 (零冗余加密)** | 原生 (~200行) + `utls` |
| **3** | **Shadowsocks**| 纯 TCP / UDP | AEAD (AES-128/256-GCM, SS-2022) | ⭐⭐⭐ | **1 RTT** | **< 1.0 MB** | **极低 (对称运算极快)** | `shadowsocks/go-shadowsocks2` |
| **4** | **Trojan** | TCP + TLS 1.3 | 标准 TLS 证书 + SHA224 首包 | ⭐⭐⭐⭐ | 1~2 RTT | ~1.2 MB | 低 | 原生 `crypto/tls` (~100行) |
| **5** | **Hysteria2** | **定制 QUIC (UDP)**| Salamander 混淆 + BBRv2 拥塞控制 | ⭐⭐⭐⭐⭐ | **0~1 RTT** | ~4.0 MB | 中高 (依赖 GSO/GRO 优化)| `apernet/hysteria/core/v2` |
| **6** | **WireGuard** | **UDP (Noise)** | ChaCha20-Poly1305 + Curve25519 | ⭐⭐⭐ | **1 RTT** | ~2.0 MB | **极低 (用户态轻量化)** | `wireguard-go` + `water` |
| **7** | **MASQUE** | **HTTP/3 (QUIC)**| IETF RFC 9298/9484 CONNECT-UDP | ⭐⭐⭐⭐⭐ | **0~1 RTT** | ~3.5 MB | 低 | `quic-go/http3` |
| **8** | **SOCKS** | 纯 TCP / UDP | 明文传输 (支持 RFC 1929 账密) | ⭐ | 1 RTT | **< 500 KB** | **微乎其微 (零加密开销)** | 原生自研 (~150行) |
| **9** | **HTTP** | 纯 TCP / TLS | HTTP/1.1 或 H2 CONNECT 隧道 | ⭐⭐ | 1~2 RTT | **< 800 KB** | **微乎其微** | 原生 `net/http` + `http2` |
| **10**| **TUIC** | **标准 QUIC (UDP)**| 原生 QUIC 0-RTT 握手 + TLS 1.3 | ⭐⭐⭐⭐ | **0 RTT (极速)** | ~3.0 MB | 低 | `EAimTY/tuic` 或 `sing-quic` |
| **11**| **Anytls** | 任意 TCP + TLS | 任意协议套用标准 TLS 1.3 管道 | ⭐⭐⭐⭐ | 1~2 RTT | ~1.5 MB | 低 | 原生 `crypto/tls` + `utls` |
| **12**| **NaïveProxy**| TCP + HTTP/2/H3 | Chromium JA3/JA4 指纹 + H2 CONNECT | ⭐⭐⭐⭐⭐ | 1~2 RTT | ~2.5 MB | 低 | `refraction-networking/utls` |

---

## 4. 设计模式全景架构建模 (Microkernel & Strategy Architecture)

若将 12 种协议的代码强行揉在主调度器中，代码必将迅速腐化并失去维护性。我们采用**微内核架构 (Microkernel) + 正交策略模式 (Orthogonal Strategy)** 进行系统建模：

```mermaid
classDiagram
    direction TB

    class IOutboundDialer {
        <<interface>>
        +Protocol() string
        +DialContext(ctx context.Context, network string, target string) (net.Conn, error)
        +DialThrough(ctx context.Context, baseConn net.Conn, target string) (net.Conn, error)
    }

    class IPacketOutboundDialer {
        <<interface>>
        +DialPacket(ctx context.Context, network string, target string) (net.PacketConn, error)
    }

    class OutboundRegistry {
        -factories map[string]OutboundFactory
        +Register(protocol string, factory OutboundFactory)
        +Create(cfg UpstreamNode) (IOutboundDialer, error)
    }

    class TransportStrategy {
        <<interface>>
        +DialTransport(ctx context.Context, serverAddr string) (net.Conn, error)
    }

    class SecurityStrategy {
        <<interface>>
        +Handshake(ctx context.Context, baseConn net.Conn, sni string) (net.Conn, error)
    }

    class BaseProtocolAdapter {
        #transport TransportStrategy
        #security SecurityStrategy
        +DialContext()
        +DialThrough()
    }

    class VlessAdapter { }
    class VmessAdapter { }
    class ShadowsocksAdapter { }
    class TrojanAdapter { }
    class Hysteria2Adapter { }
    class WireGuardAdapter { }
    class MasqueAdapter { }
    class TuicAdapter { }
    class NaiveProxyAdapter { }

    IOutboundDialer <|.. BaseProtocolAdapter
    IPacketOutboundDialer <|.. BaseProtocolAdapter
    BaseProtocolAdapter <|-- VlessAdapter
    BaseProtocolAdapter <|-- VmessAdapter
    BaseProtocolAdapter <|-- ShadowsocksAdapter
    BaseProtocolAdapter <|-- TrojanAdapter
    BaseProtocolAdapter <|-- Hysteria2Adapter
    BaseProtocolAdapter <|-- WireGuardAdapter
    BaseProtocolAdapter <|-- MasqueAdapter
    BaseProtocolAdapter <|-- TuicAdapter
    BaseProtocolAdapter <|-- NaiveProxyAdapter

    BaseProtocolAdapter o-- TransportStrategy
    BaseProtocolAdapter o-- SecurityStrategy

    class StandardTlsSecurity { }
    class UtlsBrowserSecurity { }
    class RealitySecurity { }

    SecurityStrategy <|.. StandardTlsSecurity
    SecurityStrategy <|.. UtlsBrowserSecurity
    SecurityStrategy <|.. RealitySecurity

    class TcpTransport { }
    class WebSocketTransport { }
    class GrpcTransport { }
    class QuicTransport { }

    TransportStrategy <|.. TcpTransport
    TransportStrategy <|.. WebSocketTransport
    TransportStrategy <|.. GrpcTransport
    TransportStrategy <|.. QuicTransport

    OutboundRegistry ..> IOutboundDialer : 动态工厂实例化
```

### 4.1 协议核心三要素解耦（三维正交架构）
每一个出站连接由三个正交维度自由装配组合而成：
1. **应用协议维度 (Application Protocol)**：决定应用层报文结构（如 VLESS、Trojan、Shadowsocks）；
2. **底层传输维度 (Transport Carrier)**：决定载荷在物理网络如何承载（如裸 TCP、WebSocket、gRPC、标准 QUIC）；
3. **安全与指纹维度 (Security & Impersonation)**：决定加密与伪装指纹（如标准 TLS 1.3、uTLS Chrome 浏览器指纹、VLESS Reality 借牌）。

**代码收益**：
新增一个 `VLESS + gRPC + Reality` 的节点，仅需在配置中声明：
```json
{
  "type": "vless",
  "transport": "grpc",
  "security": "reality",
  "server": "1.2.3.4",
  "port": 443
}
```
内核会自动从策略池中抓取 `GrpcTransport` 与 `RealitySecurity` 装配进 `VlessAdapter`，**彻底消灭代码重复**。

---

## 5. 核心开源库集成与技术选型矩阵

为了保证微服务编译出的二进制依然保持小巧（目标：Alpine Linux ELF < 35MB）且避免臃肿的 CGO 绑定，核心库选型原则如下：

| 协议领域 | 推荐开源库 | 权威背景 | 选型结论与决策理由 |
| :--- | :--- | :--- | :--- |
| **SOCKS4/5 & HTTP** | 原生 Go `net/http` + `golang.org/x/net/proxy` | 官方标准库 | **100% 原生自研**。零外部依赖，可靠度最高。 |
| **Shadowsocks** | `github.com/shadowsocks/go-shadowsocks2` | Shadowsocks 官方 | **集成**。纯 Go 实现 AEAD，轻巧可靠。 |
| **VLESS / VMess** | 原生轻量实现 + `github.com/sagernet/sing-vmess` | Sing-Box 生态 | **VLESS 原生自研** (~200行)；VMess 仅引入精简编解码库。 |
| **浏览器指纹 (JA3/JA4)** | `github.com/refraction-networking/utls` | 密歇根大学开源 | **集成 (核心)**。NaïveProxy 与 Reality 拟态 Chrome 的基石。 |
| **Hysteria 2** | `github.com/apernet/hysteria/core/v2` | Hysteria 官方 | **集成**。原生集成高吞吐 BBRv2 与 Salamander 混淆引擎。 |
| **QUIC 基础栈 (MASQUE/TUIC)** | `github.com/quic-go/quic-go` | IPFS / Go QUIC 事实标准 | **集成**。承载 MASQUE HTTP/3 Datagram 与 TUIC v5 传输。 |
| **WireGuard** | `golang.zx2c4.com/wireguard/wireguard-go` | WireGuard 官方 | **集成**。纯 Go 用户态协议栈，配合 TUN 或 gVisor 运行。 |

---

## 6. 当前 LWT 系统基础设施复用分析

本套全协议矩阵完全建立在 LWT 已有的成熟系统基础设施之上：
1. **宿主特权底座 (`/dev/net/tun`)**：容器具备特权并已成功运行 EasyTier，WireGuard 与透明网关天然具备运行条件；
2. **宿主双端口感知 (`PluginStandardEnvBuilder`)**：微服务自动感知 `LWT_HOST_INTERNAL_PORT`（5270 内部极速直通）与 `LWT_HOST_EXTERNAL_PORT`（5271 外部客户端访问）；
3. **零 GC 内存缓冲池 (`BridgeDuplexStream`)**：现有的 32KB `sync.Pool` 内存池直接无缝复用于所有 TCP/TLS/QUIC 流的全双工对拷；
4. **统一 Webview 控制台与暗黑主题**：ESM + Tailwind 原生前端已就绪，仅需在“添加节点”弹窗中扩充协议下拉选项；
5. **秒级在线热更流水线**：开发与测试全程使用 `-HotUpdatePlugin socks5-forwarder`，**5~8 秒完成编译替换与平滑重启，零容器停机**！

---

## 7. 配置契约全景升级设计 (`config.json` v3)

以下展示支持 12 种协议的完整声明式配置契约示例：

```json
{
  "$schema": "https://linuxwebtool.local/schemas/socks5-forwarder-v3.json",
  "version": 3,
  "upstreams": [
    {
      "id": "node-vless-reality",
      "name": "香港 Reality 极速节点",
      "type": "vless",
      "server": "hk.example.com",
      "port": 443,
      "uuid": "01234567-89ab-cdef-0123-456789abcdef",
      "security": "reality",
      "reality": {
        "sni": "www.apple.com",
        "publicKey": "base64_public_key_placeholder",
        "shortId": "0123456789abcdef"
      }
    },
    {
      "id": "node-hysteria2",
      "name": "日本 Hy2 极速抗丢包节点",
      "type": "hysteria2",
      "server": "jp.example.com",
      "port": 443,
      "auth": "your_secure_password_here",
      "obfs": { "type": "salamander", "password": "obfs_secret_here" },
      "bandwidth": { "up": "50 mbps", "down": "200 mbps" }
    },
    {
      "id": "node-wireguard",
      "name": "欧洲 WireGuard 企业对等节点",
      "type": "wireguard",
      "server": "eu.example.com",
      "port": 51820,
      "privateKey": "base64_private_key_placeholder",
      "peerPublicKey": "base64_peer_key_placeholder",
      "localAddress": ["10.0.0.2/32"]
    },
    {
      "id": "node-naiveproxy",
      "name": "美国 NaïveProxy (Chrome JA4 指纹)",
      "type": "naiveproxy",
      "server": "us.example.com",
      "port": 443,
      "username": "client_user",
      "password": "client_password_here",
      "tls": { "sni": "us.example.com", "fingerprint": "chrome" }
    },
    {
      "id": "node-tuic",
      "name": "新加坡 TUIC v5 (0-RTT 节点)",
      "type": "tuic",
      "server": "sg.example.com",
      "port": 8443,
      "uuid": "01234567-89ab-cdef-0123-456789abcdef",
      "password": "your_tuic_password_here",
      "congestionControl": "bbr"
    },
    {
      "id": "node-masque",
      "name": "Cloudflare / Apple MASQUE 节点",
      "type": "masque",
      "server": "masque.example.com",
      "port": 443,
      "token": "bearer_token_placeholder"
    }
  ]
}
```

---

## 8. 分批落地与实施建议

一次性塞入 12 种协议容易引发依赖冲突与验证周期过长，建议采取**“高频主流先行、高抗封锁紧跟、全协议收官”**的三批次稳健迭代路线：

```mermaid
timeline
    title 12 种协议分批平滑演进蓝图
    第一批 (高频主流) : 标准 SOCKS4/5 & HTTP/HTTPS : Shadowsocks (AEAD & SS-2022) : Trojan (标准 443 伪装) : VLESS (TCP + TLS)
    第二批 (高抗封锁与指纹) : VLESS Reality (uTLS 借牌免域名) : NaïveProxy (uTLS Chrome 浏览器指纹) : Anytls (通用混淆管道) : VMess (旧版节点兼容)
    第三批 (现代 QUIC 与 UDP 矩阵) : Hysteria2 (定制 QUIC + BBRv2) : TUIC v5 (0-RTT 极速握手) : WireGuard (用户态隧道) : MASQUE (RFC 9298/9484 企业网关)
```

1. **第一批（高频主流：SOCKS / HTTP / Shadowsocks / Trojan / VLESS）**：
   - 解决 85% 用户的核心出站需求；
   - 纯 Go 模块轻量，首期交付周期短（2~3 天）。
2. **第二批（高抗封锁与指纹：VLESS Reality / NaïveProxy / Anytls / VMess）**：
   - 引入 `uTLS`，彻底解决 JA3/JA4 指纹探测与域名被阻断问题；
   - 彻底摆脱必须自建域名的门槛。
3. **第三批（现代 QUIC 与 UDP 矩阵：Hysteria2 / TUIC / WireGuard / MASQUE）**：
   - 引入 `quic-go` 与 `wireguard-go`；
   - 攻克恶劣丢包网络跑满带宽、0-RTT 移动漫游与企业级 MASQUE 接入。
