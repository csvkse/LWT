# Systemd 服务智能分类（系统服务与用户服务区分）调研与架构方案

## 1. 背景与核心痛点

Systemd 作为现代 Linux 操作系统的 init 系统与服务管理器，默认托管并运行了整个系统的全部进程。
在典型的 Linux 主机上，`systemctl list-units` 通常包含 **400 到 1000+** 个单元。
当前系统将所有服务平铺展示，带来显著的运维痛点：
1. **信息噪音严重**：系统底层的 `systemd-*`、`getty@*`、`dbus*`、`polkit*` 等内部守护进程占据了 90% 以上的条目；
2. **业务定位困难**：运维人员想要查看或管理自己部署的业务服务（如 `nginx`、`docker`、`redis`、`mysql`、`easytier`、自建微服务等），必须手动在海量列表中搜索；
3. **误操作高危**：普通业务运维若误停用了 `systemd-journald`、`dbus` 或 `systemd-logind`，会导致系统挂起、登录失效甚至断网。

因此，引入**服务分级分类引擎（分类别与分层级）**，将“用户业务应用服务”与“系统核心底层服务”进行显式区分与独立过滤，具有极其重要的实用价值。

---

## 2. 深度头脑风暴：可行性、逻辑欠缺与性能瓶颈

### 2.1 双维度分类模型设计
业界对 Systemd 服务的划分通常包含两个核心维度：
1. **层级作用域维度 (Scope)**：
   - **`system` (系统全局服务)**：运行在系统总线上（System Bus），管理全局后台守护进程（默认）；
   - **`user` (用户会话服务)**：运行在当前登录用户的 Session 上（通过 `systemctl --user` 管理，存放于 `~/.config/systemd/user/`），不污染系统全局命名空间。
2. **业务领域分类维度 (Category)**：
   - **`app` (应用与用户服务 - User & Application Services)**：
     Web 服务器、数据库、中间件、容器引擎、代理与自建业务守护进程（如 `nginx`, `docker`, `mysql`, `redis`, `node`, `dotnet`, `python`, `easytier`, `frp` 等）；
   - **`system` (系统核心服务 - System Core Infrastructure)**：
     操作系统生命周期与内核配套（如 `systemd-*`, `dbus`, `init`, `cron`, `polkit`, `getty`, `rsyslog` 等）；
   - **`network` (网络与安全服务 - Network & Security)**：
     网卡调度、防火墙、SSH、VPN、域名解析（如 `NetworkManager`, `sshd`, `ufw`, `firewalld`, `resolved` 等）；
   - **`storage` (硬件监控与存储 - Storage & Hardware)**：
     磁盘阵列、文件系统、设备节点、传感器（如 `udev`, `smartd`, `multipath`, `lvm`, `fstrim` 等）。

### 2.2 逻辑欠缺与性能陷阱 (Performance & Logic Gaps)
1. **性能陷阱（严禁逐项执行 `systemctl cat`）**：
   若通过遍历每个单元去读取单元文件所在的磁盘路径（如区分 `/usr/lib/` 还是 `/etc/`），500 个 Unit 将触发 500 次子进程 fork，耗时超过 15 秒，导致 API 超时。
   *解法*：采用**高性能内存规则引擎 (Heuristic Semantic Rule Engine)**，单次 `systemctl list-units` 批量拉取后，通过预编译正则表达式与领域关键词哈希表，在内存中进行微秒级匹配分类，**整体耗时 < 1ms**。
2. **长列表分页与分类联动**：
   当用户在“全部服务”浏览至第 5 页，切换到“应用服务”（总数可能仅 15 项），若未联动状态机重置，页面会越界显示空白。
   *解法*：分类切换事件与 `usePagination` 状态机无缝绑定，自动自愈回滚至第 1 页。

---

## 3. 设计模式优化与架构设计

```mermaid
flowchart TD
    subgraph Input [原始数据采集]
        SU["systemctl list-units (或 Windows Get-Service)"]
    end

    subgraph Classifier [智能分类引擎 (Strategy Pattern)]
        CR["责任链分类器 SystemdUnitCategorizer"]
        R1["网络规则 NetworkRule"]
        R2["硬件存储规则 StorageRule"]
        R3["应用业务规则 KnownAppRule"]
        R4["系统核心规则 SystemCoreRule"]
        CR --> R1 --> R2 --> R3 --> R4
    end

    subgraph Output [分类结果与展示]
        DTO["SystemdUnitItem { Category, Scope }"]
        UI["前端 Category Tabs & 分页联动"]
    end

    SU --> Classifier
    Classifier --> DTO
    DTO --> UI
```

### 3.1 策略模式 (Strategy Pattern - 规则分类引擎)
后端建立 `ISystemdUnitCategorizer` 接口，将分类识别抽象为有序策略规则链：
1. **`NetworkSecurityRule`**：匹配 SSH、防火墙、DNS、VPN、网络管理关键字；
2. **`StorageHardwareRule`**：匹配 udev、smartd、磁盘挂载与硬件监控；
3. **`KnownApplicationRule`**：匹配常见 Web/DB/中间件/容器/用户自定义业务；
4. **`SystemCoreRule`**：匹配 `systemd-*`, `dbus`, `init`, `cron`, `polkit` 等系统内部核心；
5. **`FallbackRule`**：兜底规则。

### 3.2 适配器模式 (Adapter Pattern - 跨平台统一)
Linux 原生服务与 Windows 开发环境下的 `ServiceController` / `sc.exe` 统一适配为标准化的 `SystemdUnitItem`，均自动赋予 `Category` 与 `Scope`，保持前端 API 协议零差异。

---

## 4. 实施清单

1. **契约模型扩展**：
   - 在 [`SystemdModels.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Contracts/Features/Services/Contracts/SystemdModels.cs) 中为 `SystemdUnitItem` 增加 `Category` (`app`, `system`, `network`, `storage`, `other`) 与 `Scope` (`system`, `user`)；
   - 在 `SystemdQueryCriteria` 中支持 `Category` 与 `Scope` 过滤字段。
2. **分类器实现与依赖注入**：
   - 编写 `SystemdUnitCategorizer.cs`，实现微秒级多规则策略分类器；
   - 在 `SystemdServiceAdapter.cs`（及 Windows 降级分支）解析时注入分类器，为每个单元赋予分类属性。
3. **路由与控制器适配**：
   - 在 `ServicesController.cs` 中增加 `category` 与 `scope` 查询参数支持。
4. **前端交互与视图升级**：
   - 在 `config.js` 中扩充 `systemdUnits` 构造函数；
   - 在 `SystemdSubmodule.js` 顶部新增分类快捷筛选 Tab 栏（全部 / 🚀应用服务 / ⚙️系统核心 / 🌐网络安全 / 💾存储硬件）；
   - 在单元列表中直观展示分类徽章；
   - 过滤联动自动分页适配器，零 DOM 冗余。
5. **单元测试与门禁验证**：
   - 编写后端分类引擎测试 `SystemdUnitCategorizerTests.cs`；
   - 运行前端架构门禁与全量测试套件。
