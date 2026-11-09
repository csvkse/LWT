# LinuxWebTool 磁盘空间透视镜 (Disk Space Explorer) 插件

> **插件标识**：`plugins/disk-explorer`  
> **形态**：常驻微服务 (Resident Microservice) + ESM 响应式 Web 控制台  
> **核心语言**：Go 1.22 (单静态二进制无依赖，体积 ~6.9MB)  
> **通信协议**：HTTP REST + SSE (Server-Sent Events) 流式实时进度  

---

## 插件特色与能力

1. **多目标命名空间自适应支持**：
   - **当前系统（容器环境）**：针对容器内部沙箱文件系统进行高并发递归分析。
   - **宿主主机（物理系统）**：通过自省精准检测容器是否具备特权级及宿主 PID 1 挂载命名空间穿透能力（`/proc/1/ns/mnt` + `nsenter`）。**仅在识别到特权容器时才在前端激活宿主分析选项**，杜绝误导。
2. **极速高并发扫描引擎**：
   - **FastWalk 内核**：Go 原生 Goroutine 工作池 + `syscall.Getdents64`，压榨 NVMe SSD 并发 IOPS，百万文件分析秒级完成。
   - **GDU 加速适配器**：若环境安装了 `gdu`，优先调用其流式 JSON 输出实现超速扫描。
   - **POSIX du 适配器**：标准系统命令降级保底，实现 100% 宿主跨发行版兼容。
3. **安全与防死锁设计**：
   - **跨挂载点拦截 (`--one-file-system`)**：默认隔离网络存储卷 (NFS/CIFS/SMB) 及外部大硬盘。
   - **自动过滤伪文件系统**：跳过 `/proc/*`, `/sys/*`, `/dev/*`, `/run/*`。
   - **跳过软链接跟随**：仅统计符号链接自身元数据，根除软链接递归死锁。
4. **长任务状态机与异步治理**：
   - 任务状态机：`Idle` ➔ `Detecting` ➔ `Scanning` ➔ `Aggregating` ➔ `Completed` / `Cancelled` / `Failed`。
   - 客户端支持随时点击 `🛑 终止分析`，后台毫秒级回收进程句柄与协程，杜绝孤儿进程 I/O 狂刷磁盘。
5. **智能剪枝与双模前端呈现**：
   - 树状排行榜 + Canvas 动态矩形树图 (Squarified Treemap)。
   - 细碎文件自动折叠聚合为 `[其余 N 项微小内容: X MB]`，遵守前端高度限制门禁，防止 DOM 爆炸。
