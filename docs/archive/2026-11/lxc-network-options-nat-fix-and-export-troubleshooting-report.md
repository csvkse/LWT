# LXC 容器物理网络选项缺失、NAT 端口映射异常与导出故障深度调研与综合解决方案

> **版本**：v1.0.0  
> **状态**：已完成物理验证与根因定位 (Root Cause Identified & Verified)  
> **适用组件**：`plugins/lxc-manager/` (Go 微服务核心 + 原生 ESM Webview 前端) 及 LinuxWebTool 统一插件反向代理网关

---

## 1. 问题现象与物理真机根因定位 (Ground Truth & Root Cause)

通过内置浏览器 (Chrome DevTools MCP) 与 Debian 12 宿主机 Incus 6.0.4 联合排查，对用户反馈的 3 个问题及系统盲区进行了全链路定位：

### 1.1 问题一：前端配置网络仍无物理网络选项
- **现象**：
  在“新建 LXC 系统容器”抽屉（`drawer-create`）中，网络选择下拉框（`<select name="network">`）仅包含受管网桥 `incusbr0`，完全没有显示宿主物理网卡（如 `eth0`、`enp3s0`）或物理网络直通/桥接选项。
- **根本原因 (Root Cause)**：
  1. **前端加载数据源缺失**：前端 [`ui/app.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/ui/app.js) 的 `loadDiscovery()` 仅调用了 `populateSelect(getField('network'), (disc.networks || [])...)`，直接绑定了 Incus 内部托管的虚拟网桥列表（即 `incusbr0`），从未拉取后端已实现的物理网卡探测接口 `GET /api/network/host-interfaces`。
  2. **缺少网络类型多态分组 (`<optgroup>`)**：前端表单未区分“受管隔离 NAT 网桥（默认隔离子网）”与“物理网卡 Macvlan / 物理桥接（直接获取局域网 IP）”。
  3. **后端准入预检硬编码门禁拦截**：[`internal/incus/preflight.go`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/internal/incus/preflight.go) 第 56-64 行遍历 `d.Networks` 进行名称完全匹配校验。一旦传入物理网卡名称或 `macvlan:eth0`，预检直接抛出 `"managed bridge unavailable"` 并阻断创建。
  4. **容器创建设备树装配缺陷**：[`internal/incus/instances.go`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/internal/incus/instances.go) 第 63 行固定装配 `"eth0": {"type": "nic", "network": s.Network}`。而在 Incus/LXC 底层中，物理网卡直通必须声明为 `nictype: "macvlan"` 或 `nictype: "bridged"`，并将父网卡绑定至 `parent: "<host_nic>"`，否则 Incus 引擎会报错 `Network not found`。

### 1.2 问题二：NAT 端口映射前端打开提示异常
- **现象**：
  在实例列表卡片上点击【端口映射】（`btn-portmap`）时，界面立即弹出红色异常 Toast 提示：`加载映射失败: Cannot read properties of null (reading 'length')`，已有规则列表无法渲染。
- **根本原因 (Root Cause)**：
  1. **Go 切片序列化陷阱 (Go `nil` Slice JSON Pitfall)**：
     在 [`internal/network/portmap.go`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/internal/network/portmap.go) 中：
     ```go
     func (s *Store) List(containerID string) []PortMapping {
         var result []PortMapping // 声明为 nil 切片！
         ...
         return result
     }
     ```
     当容器初次使用或未配置任何映射规则时，`result` 为 `nil`。在 Go 标准库中，`json.Marshal(nil slice)` 会严格序列化为 **`null`**，而非空数组 `[]`。
  2. **前端非防御式编程脆弱点**：
     在 [`ui/app.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/ui/app.js) 第 644-645 行：
     ```javascript
     const list = await request(`network/mappings?container=${activePortmapContainer}`);
     if (list.length === 0) { ... } // 当 list 为 null 时抛出 TypeError！
     ```
     由于 `list` 为 `null`，访问 `.length` 触发原生异常，导致整个抽屉初始化崩溃。

### 1.3 问题三：导出功能提示“文件不存在” (404 Not Found)
- **现象**：
  点击容器卡片上的【导出】（`btn-export`）按钮时，浏览器新建下载任务或弹出提示“文件不存在”或 HTTP 404。
- **根本原因 (Root Cause)**：
  1. **反向代理网关路径遗失**：
     在 [`ui/app.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/lxc-manager/ui/app.js) 第 715 行：
     ```javascript
     a.href = `/api/instances/export?name=${encodeURIComponent(name)}`;
     ```
     当前系统为主机宿主 WebHost 反向代理架构，插件专属微服务 API 的标准入口路径必须形如 `/plugins/lxc-manager/api/...`。前端直接请求根路径 `/api/instances/export`，直接命中了主系统的 404 路由。
  2. **下载链接鉴权凭据断流**：
     使用 HTML 原生 `<a href="...">` 触发浏览器下载时，浏览器不会自动携带 `Authorization: Bearer <jwt_token>` 请求头。若未在 Query String 中补全 `?token=<jwt_token>`，即使路径正确也会被 C# WebHost 插件代理网关直接返回 401 Unauthorized。
  3. **未捕获错误导致黑盒失败**：
     通过直接点击 `<a>` 标签触发下载属于弱控制流，若服务端发生任何快照生成异常（例如底层磁盘容量不足），前端无法拦截错误文本，仅表现为浏览器端下载中断或文件不存在。

---

## 2. 业界主流方案调研与对比 (Industry Benchmark & Analysis)

| 平台 / 方案 | 物理网卡与虚拟网桥实现 | NAT 端口映射与持久化 | 实例归档流式导出与鉴权 |
| :--- | :--- | :--- | :--- |
| **Proxmox VE (PVE)** | 自动扫描 `/etc/network/interfaces`，支持 `vmbr0` (Linux Bridge)、`OVS Bridge` 与 `macvlan`，界面采用分组下拉框显式标识物理拓扑与 VLAN Tag。 | 依赖宿主 `iptables`/`nftables` PREROUTING 规则，在容器配置中定义 `pve-firewall` 转发。 | 提供 `vzdump` 原生后台任务，生成 `.tar.zst`，通过 ticket/token 校验授权下载。 |
| **Portainer (Docker/Podman)** | 网络驱动抽象为 `bridge`、`macvlan`、`ipvlan`、`host`，用户可在创建时选择 Parent NIC 与 Subnet 掩码。 | 统一管理容器端口发布 `-p host:container`，API 返回空数组 `[]` 保障 JSON 规范。 | 提供 `/containers/{id}/export` 原生流式 TAR 导出，基于 Bearer Token 鉴权。 |
| **LXD / Incus 原生 Web UI** | 扫描 `devices` 集合，支持 `nic (bridged)`、`nic (macvlan)`、`nic (routed)`、`nic (physical)`。 | 通过 `proxy` 类型设备实现端口转发（`type: proxy, listen: tcp:0.0.0.0:80, connect: tcp:127.0.0.1:80`）。 | 调用 `/1.0/instances/{name}/backups` 生成归档，流式传输并自动清理快照。 |
| **LinuxWebTool (LWT 目标态)** | **多态自适应网络适配器**：自动枚举宿主物理网卡与 Incus 虚拟网桥，支持 `incusbr0 (NAT)`、`macvlan:eth0 (物理直连)` 双模一键选择。 | **轻量内置网络映射存储**：持久化 JSON 存储，Go 后端强制空数组切片；前端防御式解包并联动宿主代理。 | **双重鉴权流式管道**：Fetch Blob 内存受控下载，Query Token + Bearer Header 双重保障，实时 Toast 进度与错误拦截。 |

---

## 3. 当前系统基础设施与可复用资源评估

当前工程已有完善的基础设施，完全无需引入厚重第三方库：

1. **宿主物理网卡探测器 (`internal/driver/hardware.go`)**：
   - 已实现 `HostNetworkDetector.DetectPhysicalInterfaces()`，能够自动扫描 `/sys/class/net`，精准提取物理网卡名称（`eth0`）、MAC 地址、运行状态（`operstate=up`）、IP 及链路速率（Mbps），可直接复用并输出给前端。
2. **Go 标准库切片初始化规范**：
   - 遵从 Go 标准库推荐的 `make([]T, 0)` 初始化惯用法，杜绝空切片被序列化为 `null` 的问题。
3. **C# WebHost 插件鉴权双通道 (`ServiceCollectionExtensions.cs`)**：
   - 主系统已原生支持从 Header 的 `Authorization` 与 Query String 的 `?token=...` / `?access_token=...` 解析 Admin 身份，前端无论采用 Ajax 还是直接流式下载均可实现无缝鉴权。
4. **前端 Toast 总线与 CSS 变量系统**：
   - 原生暗黑赛博风格 Toast 与 Form-Control 已沉淀完备，支持丰富的状态反馈。

---

## 4. 具体技术改造方案 (Detailed Solution Architecture)

### 4.1 方案 A：网络选择多态适配与物理网卡直连 (Macvlan / Bridge)

#### 1. Go 后端驱动与预检准入改造
- **`internal/incus/instances.go`**：
  扩展 `InstanceSpec`，支持物理网卡标识解析：
  - 若 `s.Network` 以 `macvlan:` 开头（如 `macvlan:eth0`）或传入已识别的宿主物理网卡：
    装配设备树为：
    ```go
    "devices": map[string]map[string]string{
        "root": {"type": "disk", "path": "/", "pool": s.StoragePool, "size": fmt.Sprintf("%dGiB", s.DiskGiB)},
        "eth0": {
            "type":    "nic",
            "nictype": "macvlan",
            "parent":  parentNic, // 提取出的物理网卡名，如 eth0
        },
    }
    ```
  - 若为普通受管网桥（如 `incusbr0`）：
    装配为标准受管模式：`{"type": "nic", "network": s.Network}`。
- **`internal/incus/preflight.go`**：
  放宽预检匹配：除匹配 `d.Networks` 虚拟受管网桥外，若 `s.Network` 为物理网卡或 `macvlan:` 格式，只要该物理网卡存在且处于 UP 状态，即判定为准入合法。

#### 2. 前端界面多态分组展示 (`ui/index.html` & `ui/app.js`)
- 在初始化或刷新时，同时请求 `network/host-interfaces` 与 `discovery`：
- 使用 `<optgroup>` 动态渲染：
  ```html
  <select name="network" class="form-control" required>
    <optgroup label="🌐 受管隔离网桥 (NAT 隔离虚拟子网 - 推荐)">
      <option value="incusbr0" selected>incusbr0 (受管网桥 · 10.0.3.1/24)</option>
    </optgroup>
    <optgroup label="🔌 物理网卡直连 (Macvlan 模式 · 直接获取局域网物理 IP)">
      <option value="macvlan:eth0">eth0 (物理网卡 · 10.126.1.70 · 10000 Mbps)</option>
    </optgroup>
  </select>
  ```
- 在实例编辑抽屉（`drawer-edit`）的网络 Tab 中，同时展示当前网卡的工作模式（NAT 网桥 vs 物理网卡直连）。

---

### 4.2 方案 B：NAT 端口映射空切片防御与鲁棒性加固

1. **后端修复 (`internal/network/portmap.go`)**：
   将 `List` 方法中的切片声明从 `var result []PortMapping` 改为：
   ```go
   result := make([]PortMapping, 0)
   ```
   确保序列化永远为 `[]`，彻底消除 `null`。
2. **前端双重防御 (`ui/app.js`)**：
   在 `loadPortMappings()` 中增加容错解包：
   ```javascript
   const res = await request(`network/mappings?container=${activePortmapContainer}`);
   const list = Array.isArray(res) ? res : [];
   ```

---

### 4.3 方案 C：实例导出异步受控下载与网关对齐

1. **前端路径与鉴权修复 (`ui/app.js`)**：
   重构 `triggerExport(name)`：
   ```javascript
   async function triggerExport(name) {
     showToast(`正在从底层建立快照并导出容器 ${name}...`);
     try {
       const url = `/plugins/lxc-manager/api/instances/export?name=${encodeURIComponent(name)}&token=${encodeURIComponent(token)}`;
       const res = await fetch(url, { headers });
       if (!res.ok) {
         const errText = await res.text();
         throw new Error(errText || `HTTP ${res.status}`);
       }
       const blob = await res.blob();
       const downloadUrl = URL.createObjectURL(blob);
       const a = document.createElement('a');
       a.href = downloadUrl;
       a.download = `${name}-backup.tar.gz`;
       document.body.appendChild(a);
       a.click();
       document.body.removeChild(a);
       URL.revokeObjectURL(downloadUrl);
       showToast(`容器 ${name} 备份归档导出成功！`);
     } catch (err) {
       showToast(`导出失败: ${err.message}`, true);
     }
   }
   ```
2. **优势**：
   - 精确拼接 `/plugins/lxc-manager/api/...`，通过 C# WebHost 代理网关；
   - 携带 `Authorization` 头与 Query Token 双重凭据；
   - 捕获 HTTP 错误并在界面给出具体 Toast 说明，避免黑盒失败。

---

## 5. 防回归门禁与自动化测试规范 (Guardrails & Regression Gates)

为确保同类问题永不复发，确立以下 4 重门禁：

1. **门禁规则 FE-API-NULL-DEFENSE (前端防 NULL 列表解包门禁)**：
   静态语法与单测门禁扫描前端所有 `await request(...)` 赋值点，凡访问 `.length`、`.map`、`.forEach` 之前未做 `Array.isArray` 或 `|| []` 兜底保护的，直接阻断构建。
2. **门禁规则 BE-JSON-EMPTY-SLICE (后端空切片序列化门禁)**：
   Go 接口单测必须覆盖空数据场景，断言 HTTP 响应体必须为 `[]` 而不是 `null`。
3. **门禁规则 FE-PLUGIN-URL-PREFIX (前端插件 API 路径门禁)**：
   前端代码中严禁裸露调用 `/api/...` 作为插件内部路由，强制要求统一前缀 `/plugins/<id>/api/...`。
4. **DevTools MCP 真机自动化回归用例**：
   - 自动点击打开【端口映射】抽屉，断言 Toast 无异常、列表渲染 `暂无转发规则`。
   - 自动拉取【新建容器】网络下拉框，断言至少包含 1 个受管网桥与 1 个物理网卡选项。
   - 自动化触发【导出】接口，验证返回 HTTP 200 及 `application/gzip` 流。
