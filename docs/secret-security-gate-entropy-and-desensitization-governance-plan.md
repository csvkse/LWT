# 凭据防泄露门禁：多字符集自适应香农熵与脱敏规则合规校验调研与架构方案

> **文档状态**：已批准并执行落地  
> **文档定位**：针对 LWT 敏感凭据防泄露门禁系统的深度演进调研，重点攻关**多字符集自适应香农熵检测算法**、**规范化脱敏规则合规性强校验**、**假脱敏/半脱敏泄露防御**以及**公网真实地址治理**，给出完备的理论模型、性能基准推演与落地代码实现。

---

## 目录

1. [背景与核心问题调研](#1-背景与核心问题调研)
   - [1.1 现有防泄露门禁现状评估](#11-现有防泄露门禁现状评估)
   - [1.2 现有香农熵算法缺陷分析](#12-现有香农熵算法缺陷分析)
   - [1.3 现有脱敏机制的盲区与假脱敏风险](#13-现有脱敏机制的盲区与假脱敏风险)
2. [信息论与香农熵自适应算法建模](#2-信息论与香农熵自适应算法建模)
   - [2.1 香农熵与字符集理论上限推导](#21-香农熵与字符集理论上限推导)
   - [2.2 归一化度量熵 (Metric Entropy) 模型](#22-归一化度量熵-metric-entropy-模型)
   - [2.3 多字符集阈值与分词策略矩阵](#23-多字符集阈值与分词策略矩阵)
3. [脱敏合规校验与防伪脱敏机制建模](#3-脱敏合规校验与防伪脱敏机制建模)
   - [3.1 规范化占位符遵从性校验 (`AGENT.md` 规范)](#31-规范化占位符遵从性校验-agentmd-规范)
   - [3.2 假脱敏与半脱敏 (Pseudo-Masking) 拦截策略](#32-假脱敏与半脱敏-pseudo-masking-拦截策略)
   - [3.3 公网真实 IP 与非标私有动态域名治理 (RFC 5737 / RFC 1918)](#33-公网真实-ip-与非标私有动态域名治理-rfc-5737--rfc-1918)
4. [性能延迟评估与设计模式抽象](#4-性能延迟评估与设计模式抽象)
   - [4.1 性能预算与分词预筛选机制 (< 200ms)](#41-性能预算与分词预筛选机制--200ms)
   - [4.2 设计模式扩展：策略、适配器与组合过滤器](#42-设计模式扩展策略适配器与组合过滤器)
5. [落地规则清单与架构门禁集成](#5-落地规则清单与架构门禁集成)
   - [5.1 门禁规则集升级](#51-门禁规则集升级)
   - [5.2 架构测试 `LinuxArch068` 联动守护](#52-架构测试-linuxarch068-联动守护)
6. [验证与验收标准](#6-验证与验收标准)

---

## 1. 背景与核心问题调研

### 1.1 现有防泄露门禁现状评估

在工程前序阶段，我们建立了初步的凭据安全门禁体系：
- **`.githooks/security-gate.cjs`**：支持 `--staged`（单进程差异流解析）与 `--full`（全仓受控文件扫描）。
- **`LinuxArch068`**：在 `LinuxWebTool.ArchitectureTests` 中对规则完整性、Git 忽略目录隔离及全仓退出码做自动化断言。
- **已部署基础规则**：`SEC-KNOWN-LEAKS`（已知历史泄露字串）、`SEC-TUNNEL-ID`（隧道 ID）、`SEC-OPENAI-KEY`（LLM Key）、`SEC-PRIVATE-KEY`（私钥头部）、`SEC-ADMIN-PASSWORD`（管理员密码定义）、`SEC-EASYTIER-SECRET`（组网凭据）、`SEC-HIGH-ENTROPY-SECRET`（初级香农熵规则）。

### 1.2 现有香农熵算法缺陷分析

经过深度代码审计，现有香农熵算法存在以下**三大理论与工程缺陷**：

1. **绑定变量名的窄视角匹配，漏报自由格式与行内 Token**：
   现有代码使用：
   ```javascript
   const re = /(?:api_?key|auth_?token|preshared_?key|secret_?key)\s*[:=]\s*["']([^"']{16,})["']/gi;
   ```
   如果凭据以 Bearer 标头（`Bearer eyJ...`）、未命名命令行参数（`--token=...`）、连接字符串（`postgres://user:high_entropy_pass@host`）、YAML 字典键名（`credential:`）、或 Markdown 引用块直接出现，该规则将**完全漏报**。

2. **单一固定阈值（3.65）引发的“字符集失真”**：
   - **十六进制（Hex，Base16）**：字符集大小 $|\Sigma|=16$，理论香农熵上限为 $\log_2(16) = 4.0$ 比特。一个高度随机的 32 字节哈希/密钥，其实际香农熵通常分布在 **$3.2 \sim 3.8$** 之间。采用 3.65 作为硬阈值会导致接近半数的随机十六进制私钥漏报！
   - **Base64**：字符集大小 $|\Sigma|=64$，理论香农熵上限为 $\log_2(64) = 6.0$ 比特。普通的英文代码或 Base64 编码的短词汇香农熵可轻易达到 $3.7$，使用 3.65 阈值会导致误报率激增；而真正随机的 Base64 凭据熵值通常高达 **$4.6 \sim 5.8$**。
   - **结论**：脱离字符集基数（Alphabet Size）的静态绝对熵值度量是不可靠的。

3. **缺乏分词与滑动窗口能力**：
   面对长行（如 Minified 前端资源、长日志、SQL 数据行），如果不对字符串进行 Token 级离散分片计算，会导致整行混合计算被稀释，或误将合法长文本判为高熵异常。

### 1.3 现有脱敏机制的盲区与假脱敏风险

现存门禁在处理“脱敏数据”时，仅采用被动的关键词排除（如 `includes('...')`、`includes('***')`、`MOCK_LEXICON`）。这导致了严重的安全隐患：

1. **假脱敏 / 危险半脱敏（Pseudo-Masking / Semi-Masking）**：
   开发者或 AI 在编写文档或提交调试记录时，常常误以为“加几个星号或省略号就是脱敏”。例如：
   ```markdown
   sk-live-unsafe-token-sample-123456789...  (仅在尾部加省略号，真实密钥已全部暴露)
   tunnel_mock_bad_pattern_example***  (前缀与伪脱敏泄露)
   admin_password = "uFkT-demo-sample***"     (暴露高熵密码片段，暴力破解复杂度降为常数级)
   ```
   **现有门禁遇到 `...` 或 `***` 立即直接放行，形成了事实上的“掩耳盗铃式安全后门”！**

2. **缺乏标准占位符合规性校验（Standard Compliance）**：
   《AGENT.md》第 7 节明确规定了标准占位符（如 `your_secure_password_here`、`tunnel_0123456789abcdef0123456789abcdef`、`demo_secret_token`、`demo_net`、`easytier-peer.example.com`）。门禁应主动校验敏感参数的取值是否严格符合规范，防止自造混乱。

3. **公网真实 IP 与私有动态域名缺乏白名单与脱敏检测**：
   在架构中，应确保文档与测试用例中严禁出现开发者的公网服务器真实 IPv4（如阿里云/腾讯云/VPS 公网 IP），以及私有个人动态域名（如 `*.xyz`、`*.top`、`*.dynv6.net`、`*.f3322.net`）。所有示例网络地址必须严格脱敏为 RFC 5737 文档专用保留网段（`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`）或 RFC 2606 示例域名（`example.com`）。

---

## 2. 信息论与香农熵自适应算法建模

### 2.1 香农熵与字符集理论上限推导

根据香农信息论（Shannon Information Theory），离散随机变量 $X$ 的香农熵（Shannon Entropy）定义为：

$$H(X) = - \sum_{i=1}^{n} P(x_i) \log_2 P(x_i)$$

其中 $P(x_i)$ 为字符 $x_i$ 在候选字符串中出现的经验概率。

当且仅当字符串中的所有字符均独立且均匀分布在大小为 $|\Sigma|$ 的字符集（Alphabet）中时，$H(X)$ 达到理论最大值：

$$H_{max}(\Sigma) = \log_2(|\Sigma|)$$

下表推导了常见凭据编码形式的理论极值：

| 字符集类别 | 典型字符集构成 | 字符集大小 $|\Sigma|$ | 理论最大熵 $H_{max}$ | 典型真凭据熵值分布 |
| :--- | :--- | :---: | :---: | :---: |
| **Hexadecimal (Base16)** | `[0-9a-fA-F]` | 16 | **4.000** bits | **$3.20 \sim 3.90$** |
| **Base64 (Standard/URL)** | `[A-Za-z0-9+/=]` 或 `[A-Za-z0-9_-]` | 64 | **6.000** bits | **$4.50 \sim 5.85$** |
| **Alphanumeric (Base62)** | `[A-Za-z0-9]` | 62 | **5.954** bits | **$4.20 \sim 5.60$** |
| **ASCII Printable** | `[\x20-\x7E]` | 95 | **6.570** bits | **$4.80 \sim 6.20$** |

### 2.2 归一化度量熵 (Metric Entropy) 模型

为了消除不同字符集因长度与基数差异带来的度量失真，引入**字符集自适应归一化度量熵（Metric Entropy）**：

$$H_{metric}(X) = \frac{H(X)}{\log_2(|\Sigma_{detected}|)}$$

其中 $|\Sigma_{detected}|$ 为分析器根据 Token 构造成分自适应判定的有效字符集大小：
- 若 Token 纯由 Hex 构成（满足 `/^[0-9a-fA-F]+$/`），则 $|\Sigma_{detected}| = 16$；
- 若 Token 包含字母、数字及典型 Base64 字符，则 $|\Sigma_{detected}| = 64$；
- 其余混合凭据判定为 Alphanumeric 或 ASCII。

通过该模型，可以将所有字符集的熵值映射到 $[0, 1.0]$ 的统一标尺上。

### 2.3 多字符集阈值与分词策略矩阵

结合工程实践与降低误报（FP）的要求，确立双轨判定矩阵（绝对香农熵 + 归一化度量熵 + 最小有效长度）：

```mermaid
flowchart TD
    RawToken["候选 Token (长度 >= 16)"] --> FastFilter{"排除已知安全模式 (Mock/UUID/Guid/常量)"}
    FastFilter -- "命中文档/测试词典" --> SafePass["放行 (Safe)"]
    FastFilter -- "未命中文档词典" --> Classify{"字符集自动分类"}
    
    Classify -- "全 Hex 字符 [0-9a-fA-F]" --> HexCheck{"长度 >= 24 且 (熵 > 3.25 或 Metric > 0.81) ?"}
    HexCheck -- "Yes" --> ViolationHex["🚨 拦截: 高熵 Hex 凭据泄露"]
    HexCheck -- "No" --> SafePass
    
    Classify -- "Base64 字符 [A-Za-z0-9+/_-]" --> B64Check{"长度 >= 20 且 (熵 > 4.55 或 Metric > 0.76) ?"}
    B64Check -- "Yes" --> ViolationB64["🚨 拦截: 高熵 Base64/Token 凭据泄露"]
    B64Check -- "No" --> SafePass
    
    Classify -- "通用字母数字混合" --> AlphaCheck{"长度 >= 16 且 (熵 > 4.10) ?"}
    AlphaCheck -- "Yes" --> ViolationAlpha["🚨 拦截: 高熵未知凭据泄露"]
    AlphaCheck -- "No" --> SafePass
```

---

## 3. 脱敏合规校验与防伪脱敏机制建模

### 3.1 规范化占位符遵从性校验 (`AGENT.md` 规范)

严格对标《AGENT.md》第 7 节“凭据安全红线与脱敏规范”，对代码与文档中出现的敏感配置参数做合规性比对：

| 敏感配置项 | 合规允许模式 | 违规判定策略 | 违规提示与修复建议 |
| :--- | :--- | :--- | :--- |
| **`tunnel_id` / `tunnel_...`** | `tunnel_0123456789abcdef0123456789abcdef`<br>`tunnel_12345678901234567890123456789012`<br>`tunnel_00000000000000000000000000000000` | 包含 `tunnel_[a-z0-9]{32}` 但不属于上述三种 | 必须强制替换为 `tunnel_0123456789abcdef0123456789abcdef` |
| **`admin_password` / `web_admin_password`** | `your_secure_password_here`<br>动态表达式（`process.env.*`, `$WEB_ADMIN_PASSWORD`）<br>单元测试通用占位（`mock`, `test`） | 非空明文且长度 $\ge 6$，不包含标准占位 | 必须替换为 `your_secure_password_here` 或引用环境变量 |
| **`network_secret` (EasyTier)** | `demo_secret_token`<br>`demo_net`<br>动态表达式（`entity.NetworkSecret`） | 任意真实硬编码字符（如历史真实凭据） | 必须替换为 `demo_secret_token` |
| **EasyTier Peer 对端节点** | `easytier-peer.example.com`<br>官方公共中继（`public.easytier.top:11010`）<br>局域网/回环地址（`127.0.0.1`, `10.x.x.x`, `192.168.x.x`） | 未经脱敏的个人私有域名或公网 IPv4 | 必须脱敏为 `easytier-peer.example.com` |

### 3.2 假脱敏与半脱敏 (Pseudo-Masking) 拦截策略

针对在未脱敏凭据后方或中间随意追加 `...`、`***`、`xxx` 的现象，建立专门的假脱敏检测策略（`SEC-PSEUDO-MASKING`）：

```javascript
// 核心逻辑：若行内包含掩码符号 (*, ., x)，但同时存在未掩码部分且：
// 1. 连续未掩码子串长度 >= 8；
// 2. 属于敏感前缀（如 sk-live-, tunnel_, gh_pat_, xoxb-）；
// 3. 或连续未掩码部分香农熵 >= 3.2。
// 则判定为危险的假脱敏/半脱敏泄露！
```

**拦截案例对比**：
- ❌ `sk-live-unsafe-token-sample-123456789...` $\rightarrow$ **直接阻断**（判定为半脱敏，前缀暴露随机 Token 片段）。
- ❌ `tunnel_mock_bad_pattern_example***` $\rightarrow$ **直接阻断**（判定为假脱敏，实际 Tunnel ID 结构泄露）。
- ❌ `admin_password = "uFkT-demo-sample***"` $\rightarrow$ **直接阻断**（判定为假脱敏，密码暴露高熵片段）。
- ✅ `sk-proj-***` $\rightarrow$ **放行**（仅保留 SDK 类型前缀，未暴露具体随机字符）。
- ✅ `your_secure_password_here` $\rightarrow$ **放行**（规范占位符）。
- ✅ `tunnel_0123456789abcdef0123456789abcdef` $\rightarrow$ **放行**（规范占位符）。

### 3.3 公网真实 IP 与非标私有动态域名治理 (RFC 5737 / RFC 1918)

#### 1. IPv4 地址分类合规性引擎

建立基于 CIDR 前缀与数值解析的高速 IP 过滤器：

| IP 类别 | 范围 | 合规判定 | 说明 |
| :--- | :--- | :---: | :--- |
| **本机环回** | `127.0.0.0/8` | ✅ **放行** | 开发、测试、容器自检标准入口 |
| **未指定与广播** | `0.0.0.0`, `255.255.255.255` | ✅ **放行** | 监听绑定与广播地址 |
| **私有内网 (RFC 1918)** | `10.0.0.0/8`<br>`172.16.0.0/12`<br>`192.168.0.0/16` | ✅ **放行** | 局域网管理、Docker 网桥、EasyTier 虚拟网段 |
| **文档与示例专用 (RFC 5737)** | `192.0.2.0/24` (TEST-NET-1)<br>`198.51.100.0/24` (TEST-NET-2)<br>`203.0.113.0/24` (TEST-NET-3) | ✅ **放行** | **IETF 官方推荐的规范化脱敏 IP** |
| **链路本地 (RFC 3927)** | `169.254.0.0/16` | ✅ **放行** | 自动专用 IP 寻址 |
| **基准测试 (RFC 2544)** | `198.18.0.0/15` | ✅ **放行** | 网络基准测试网段 |
| **公共合规服务** | `8.8.8.8`, `1.1.1.1`, `1.2.3.4` (测试夹具) | ✅ **放行** | 著名公共 DNS 与标准测试桩 |
| **非合规公网 IPv4** | 任意其他公共公网地址 | ❌ **阻断** | 疑似泄露开发者 VPS/云服务器公网 IP，须替换为 RFC 5737 地址 |

#### 2. 非标私有动态域名识别引擎

拦截非官方文档保留域名、非官方公共服务、带有个人私有穿透/DDNS 特征的域名：
- **违规特征**：包含常见个人穿透与动态解析后缀（如 `.xyz`, `.vip`, `.top`, `.dynv6.net`, `.f3322.net`, `.duckdns.org`, `.no-ip.com`），且不属于官方公共中继（`public.easytier.top`）与开源公共镜像（`ghfast.top`）。
- **合规标准**：必须改写为 RFC 2606 标准保留域名（`example.com`, `example.org`, `example.net`, `localhost`）。

---

## 4. 性能延迟评估与设计模式抽象

### 4.1 性能预算与分词预筛选机制 (< 200ms)

门禁需要在 Git 提交时高频触发，必须保证极致性能：
1. **短路预筛选（Fast Pre-check）**：
   - 先通过高效正则快速排除无敏感词且无长字符块的普通代码行。
   - 过滤注释行中的常规版权信息与 URL 路径。
2. **单次暂存区差异流（Single-Pass Diff Stream）**：
   - 维持 `git diff --cached -U0` 单次批量拉取新增行模式，避免重复执行 `git` 子进程。
3. **性能基准指标**：
   - 暂存区 200 个文件扫描耗时：$\le 180\text{ ms}$。
   - 全仓 800+ 文件扫描耗时：$\le 500\text{ ms}$。

### 4.2 设计模式扩展：策略、适配器与组合过滤器

```mermaid
classDiagram
    class ISecurityStrategy {
        <<interface>>
        +id : string
        +name : string
        +evaluate(context: LineScanContext) ViolationResult
    }
    
    class EntropyBase64Strategy {
        +evaluate() ViolationResult
    }
    class EntropyHexStrategy {
        +evaluate() ViolationResult
    }
    class PseudoMaskingStrategy {
        +evaluate() ViolationResult
    }
    class DesensitizationStandardStrategy {
        +evaluate() ViolationResult
    }
    class UnmaskedNetworkAddressStrategy {
        +evaluate() ViolationResult
    }
    
    ISecurityStrategy <|.. EntropyBase64Strategy
    ISecurityStrategy <|.. EntropyHexStrategy
    ISecurityStrategy <|.. PseudoMaskingStrategy
    ISecurityStrategy <|.. DesensitizationStandardStrategy
    ISecurityStrategy <|.. UnmaskedNetworkAddressStrategy
    
    class ShannonEntropyCalculator {
        +calculate(str: string) float
        +calculateMetric(str: string, charsetSize: int) float
        +detectCharset(str: string) CharsetEnum
    }
    
    class IpClassifier {
        +isRfc1918(ip: string) bool
        +isRfc5737(ip: string) bool
        +isLoopback(ip: string) bool
        +isAllowedPublicIp(ip: string) bool
    }
    
    EntropyBase64Strategy ..> ShannonEntropyCalculator
    EntropyHexStrategy ..> ShannonEntropyCalculator
    PseudoMaskingStrategy ..> ShannonEntropyCalculator
    UnmaskedNetworkAddressStrategy ..> IpClassifier
```

---

## 5. 落地规则清单与架构门禁集成

### 5.1 门禁规则集升级

在 `.githooks/security-gate.cjs` 中将原先的 7 条规则升级为扩展的 10 条高阶策略规则：

1. **`SEC-KNOWN-LEAKS`**：已知历史泄露字串防复发（包含历史密码、隧道 ID、内部地址）。
2. **`SEC-TUNNEL-ID`**：真实隧道 ID 防泄露（只允许标准合规占位符）。
3. **`SEC-OPENAI-KEY`**：OpenAI / LLM API Key 防泄露。
4. **`SEC-PRIVATE-KEY`**：私钥物理块未脱敏防泄露。
5. **`SEC-ADMIN-PASSWORD`**：未脱敏管理员明文密码防泄露。
6. **`SEC-EASYTIER-SECRET`**：EasyTier 组网凭据防泄露。
7. **`SEC-ENTROPY-BASE64`（新增）**：多字符集 Base64 / JWT / Bearer 高熵 Token 防泄露（自适应度量熵）。
8. **`SEC-ENTROPY-HEX`（新增）**：32/64 位十六进制高熵私钥/哈希防泄露（自适应度量熵）。
9. **`SEC-PSEUDO-MASKING`（新增）**：假脱敏/危险半脱敏模式防泄露（带掩码但保留高熵前缀）。
10. **`SEC-UNMASKED-NETWORK-ADDRESS`（新增）**：公网真实 IPv4 与非标动态域名防泄露（RFC 5737 标准比对）。

### 5.2 架构测试 `LinuxArch068` 联动守护

更新 `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs` 中的 `LinuxArch068_凭据安全防御_未脱敏密码与真实地址防泄露门禁`：
- 断言规则清单中必须包含新增的 4 项核心规则：`SEC-ENTROPY-BASE64`、`SEC-ENTROPY-HEX`、`SEC-PSEUDO-MASKING`、`SEC-UNMASKED-NETWORK-ADDRESS`。
- 执行 `node .githooks/security-gate.cjs --full` 并断言退出码为 0。

---

## 6. 验证与验收标准

1. **功能完整性验证**：
   - 构造伪造高熵 Base64 凭据、高熵 Hex 私钥、半脱敏字串 `sk-live-1234567890abcdef...` 以及外网测试公网 IP `123.57.24.18`，门禁应 100% 精准拦截并报错退出。
2. **全仓零误报验证**：
   - 针对当前代码库全部 800+ 个文件执行 `node .githooks/security-gate.cjs --full`，必须 100% 绿色通过，耗时 $\le 500\text{ ms}$。
3. **暂存区极速增量验证**：
   - 执行 `node .githooks/security-gate.cjs --staged`，耗时 $\le 200\text{ ms}$。
4. **全套自动化测试回归**：
   - `LinuxArch068` 与所有 217 项架构门禁测试全绿。
   - 7 项前端架构门禁全绿。
