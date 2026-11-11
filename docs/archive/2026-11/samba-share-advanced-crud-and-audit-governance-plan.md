# Samba (SMB) 局域网共享管理 深度增删查改 (CRUD) 补齐与连接会话审计治理方案

> **归档编号**：ARCH-202611-SAMBA-SHARE-ADVANCED-CRUD  
> **创建日期**：2026-11-11  
> **状态**：已立项实施（In Progress）  
> **密级**：内部架构标准（严格遵守安全脱敏红线，无真实凭据）

---

## 1. 背景与核心问题调研 (Problem Statement)

在 LinuxWebTool（LWT）完成了特权容器 PID 1 宿主穿透与 passdb 凭据用户治理之后，针对 **Samba 共享文件夹的增、删、查、改（CRUD）**，用户提出了关键质询：
> **“Samba 共享管理 增删查改 是否存在欠缺？能否补齐？”**

经过对当前实现与主流 NAS / 运维系统的深入对标调研，当前版本在共享管理维度确实存在以下显著欠缺：

### 1.1 核心 CRUD 维度的三大结构性缺失
1. **“改 (Update)”能力严重缺失**：
   - 前端共享目录列表仅有「删除」按钮，无「编辑」按钮。
   - 用户若需修改共享路径、只读状态、访客权限、访问用户或备注，必须先删除现有共享再重新手动录入，极易误操作且体验繁琐。
2. **“增 (Create)”维度的安全与合规防御不足**：
   - 缺乏高危系统路径黑名单（Path Safety Guard）：允许用户误将 `/`、`/etc`、`/boot`、`/sys`、`/proc`、`/dev`、`/usr` 等系统核心目录作为共享路径发布，一旦开放写权限将面临宿主机核心文件被篡改或覆盖的严重风险。
   - 路径输入缺乏合法性校验与宿主目录存在性/创建性实时提示。
3. **“查 (Read)”维度的浅层化**：
   - 仅显示名称、路径和基础布尔值。
   - 缺乏宿主机物理挂载路径的磁盘可用空间（Free/Total Space）与挂载状态检测。
   - 缺乏对当前局域网活跃客户端连接（Active Sessions / Connected IP / User / Share / Protocol）的实时监控（`smbstatus`）。
4. **RESTful 契约粒度粗糙**：
   - 仅提供全局覆盖式 `POST /api/Services/Config/Samba`，缺乏针对单个共享资源的原子性 API：
     - `POST /api/Services/Config/Samba/Shares`（新增共享）
     - `GET /api/Services/Config/Samba/Shares/{name}`（获取单条详情）
     - `PUT /api/Services/Config/Samba/Shares/{name}`（修改单条共享）
     - `DELETE /api/Services/Config/Samba/Shares/{name}`（删除单条共享）

---

## 2. 业界主流方案对比与参考 (Industry Benchmarking)

调研了业界主流的 Linux 存储管理与 NAS 开源方案：**OpenMediaVault (OMV)**、**Cockpit (cockpit-file-sharing)**、**TrueNAS SCALE**、**CasaOS** 与 **Webmin**：

| 维度 | OpenMediaVault (OMV) | Cockpit File Sharing | TrueNAS SCALE | CasaOS / ZimaOS | LinuxWebTool (目标设计) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **单条共享编辑** | 完整表单，支持在线修改所有参数并重载 | 支持在线修改属性并实时测试语法 | 深度属性页（基本/高级/ACL） | 仅能修改名称与访客开关 | **完整支持新建与在线编辑，表单预填与无缝重载** |
| **ACL 细粒度控制** | 独立读写清单 (`write list`) 与只读清单 (`read list`) | `valid users` 限制 | POSIX / NFSv4 ACL | 单一密码模式 | **支持访客、有效用户集、指定只读用户与指定可写用户** |
| **网络隐藏共享** | 支持 `browseable = no` | 支持 | 支持 | 不支持 | **原生支持 `browseable` 属性（隐藏共享）** |
| **局域网防误删机制** | 内置 Samba Recycle VFS（`.recycle`） | 无 | ZFS 物理快照 | 无 | **内置一键开关 `vfs_recycle` 回收站机制** |
| **macOS 兼容优化** | 支持 `vfs_fruit` 模块 | 无 | 深度集成 AAPL / Time Machine | 无 | **内置 macOS 优化与 `.DS_Store` 冲突防御** |
| **在线连接会话审计** | 集成 `smbstatus` 仪表盘 | 无 | 完整会话审计与带宽图表 | 无 | **集成轻量化 `smbstatus` 在线客户端与连接监控** |
| **路径安全拦截** | 限制在存储卷/共享文件夹池中 | 警告系统目录 | 仅允许在 ZFS 数据集内 | 限制在挂载目录 | **内置系统高危黑名单守卫（Path Safety Guard）** |

---

## 3. 详细架构与技术设计方案 (Technical Architecture)

```
+-----------------------------------------------------------------------------------------+
|                                    Web 前端视图层                                        |
|  +--------------------------------+  +-----------------------------------------------+  |
|  |  SambaSubmodule.js             |  |  SambaShareModal.js (新建 / 编辑双模式)        |  |
|  |  - 共享列表展示 (含磁盘容量)   |  |  - 基础设置: 名称, 宿主路径, 访客, 只读       |  |
|  |  - 「✏️ 编辑」「🗑️ 删除」按钮  |  |  - 授权用户与读写白名单 (Write/Read List)     |  |
|  |  - 「📡 在线会话」卡片 (smbstatus) |  |  - 高级选项: 隐藏共享, 回收站, macOS 优化     |  |
|  +--------------------------------+  +-----------------------------------------------+  |
+--------------------------------------------+--------------------------------------------+
                                             | HTTP REST API
+--------------------------------------------v--------------------------------------------+
|                                  ASP.NET Minimal API 控制器                              |
|  - GET    /api/Services/Config/Samba/Shares/{name}                                      |
|  - POST   /api/Services/Config/Samba/Shares                                             |
|  - PUT    /api/Services/Config/Samba/Shares/{name}                                      |
|  - DELETE /api/Services/Config/Samba/Shares/{name}                                      |
|  - GET    /api/Services/Config/Samba/Sessions                                           |
+--------------------------------------------+--------------------------------------------+
                                             |
+--------------------------------------------v--------------------------------------------+
|                       SystemServiceConfigAdapter.Samba.cs                               |
|  +-----------------------------------------------------------------------------------+  |
|  | 1. Path Safety Guard: 校验拦截 /, /etc, /boot, /sys, /proc, /dev 等高危路径        |  |
|  | 2. VFS Objects Pipeline: 智能编排 recycle, fruit, streams_xattr 等模块            |  |
|  | 3. Atomic Parser & Formatter: 精确解析与重写 smb.conf 段落，保留全局配置与注释     |  |
|  | 4. Testparm Pre-flight: 候选配置写入宿主 /tmp 预检，语法合法才替换并备份           |  |
|  | 5. Host PID 1 Penetration: 通过 nsenter/host 穿透创建目录、赋权 0775、重载 smbd   |  |
|  | 6. Smbstatus Collector: 执行 smbstatus -b / -S 结构化解析客户端 IP、用户与共享      |  |
|  | 7. Disk Space Probe: 执行 df -kP 探测物理共享目录所在磁盘容量与挂载点               |  |
|  +-----------------------------------------------------------------------------------+  |
+-----------------------------------------------------------------------------------------+
```

### 3.1 共享段落参数规范（smb.conf 字段映射）

每个共享段将支持以下结构化属性映射：
```ini
[mediary-data]
   comment = 媒体数据共享
   path = /var/docker/mediary-data
   read only = no
   browseable = yes
   guest ok = no
   valid users = user1, user2
   write list = user1
   read list = user2
   create mask = 0775
   directory mask = 0775
   vfs objects = recycle
   recycle:repository = .recycle
   recycle:keeptree = yes
   recycle:versions = yes
   recycle:touch = yes
   recycle:exclude = *.tmp,~$*,Thumbs.db
```

### 3.2 路径安全防护守卫 (Path Safety Guard / ValidateSharePathSafety)
严禁将下列目录及其直接子目录发布为共享，适配器调用 `ValidateSharePathSafety` 进行强制拦截：
- `/`（根目录）
- `/etc`, `/etc/samba`, `/etc/ssh` 等系统配置目录
- `/boot`, `/sys`, `/proc`, `/dev` 系统虚拟文件系统
- `/bin`, `/sbin`, `/usr/bin`, `/usr/sbin`, `/lib`, `/lib64` 系统可执行程序与库目录
- `/root` 超级管理员家目录（除非显式子目录）

### 3.3 局域网在线连接会话审计 (`smbstatus`)
执行穿透指令：`smbstatus -b 2>/dev/null` 与 `smbstatus -S 2>/dev/null`
解析输出：
- `PID`（服务进程 PID）
- `Username`（登录用户名）
- `Machine`（客户端机器名 / IP 地址）
- `ProtocolVersion`（SMB 协议版本，如 `SMB3_11`）
- `ShareName`（挂载共享名称）
- `ConnectedAt`（连接建立时间）

---

## 4. 架构门禁与防回归测试规范 (Architecture Gates)

为杜绝未来代码重构破坏本方案的核心安全设计，新增以下架构测试与门禁：

1. **`LinuxArch084_SambaAdvancedShareCrudAndAuditGovernance`**：
   - 验证 `ISystemServiceConfigAdapter` 必须具备单条共享的增（`CreateSambaShareAsync`）、删（`DeleteSambaShareAsync`）、查（`GetSambaShareAsync`）、改（`UpdateSambaShareAsync`）及会话审计（`GetSambaSessionsAsync`）。
   - 验证 `SambaShareItem` 必须包含 `Browseable`, `WriteList`, `ReadList`, `EnableRecycleBin` 属性。
   - 验证 `SystemServiceConfigAdapter.Samba.cs` 内部必须调用高危路径安全防护检查。
   - 验证 MinimalApi 路由与 Native AOT 序列化上下文必须完整映射相关请求与响应。
2. **`FE-SAMBA-CRUD-COMPLETENESS` 前端门禁**：
   - 验证 `SambaSubmodule.js` 必须存在「编辑」按钮与「在线会话」渲染。
   - 验证 `SambaShareModal.js` 必须支持创建/编辑双向模式与高级选项折叠。

---

## 5. 执行计划 (Implementation Steps)

- [x] **Step 1**：完成技术调研、业界对标方案对比与架构文档归档。
- [ ] **Step 2**：在 `LinuxWebTool.Contracts` 中扩展共享 CRUD 与会话审计模型契约。
- [ ] **Step 3**：在 `LinuxWebTool.Infrastructure` 中实现单条共享增删改查、路径安全黑名单、VFS 回收站编排、`smbstatus` 会话嗅探与磁盘容量探测。
- [ ] **Step 4**：在 `LinuxWebTool.WebHost` 中添加控制器路由、Minimal API 映射与 AOT 上下文。
- [ ] **Step 5**：重构前端 `SambaSubmodule.js` 与 `SambaShareModal.js`，实现完整的编辑弹窗、高级选项、容量胶囊与在线会话卡片。
- [ ] **Step 6**：编写 `LinuxArch084` 架构测试，执行前端架构门禁与全仓安全凭据门禁。
- [ ] **Step 7**：使用极速开发部署管线（`-Mode Dev`）发布至测试实例，使用 Chrome DevTools MCP 与 REST API 实机验收。
