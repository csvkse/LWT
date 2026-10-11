# 文件管理页面下载功能与文件夹打包下载调研及实施方案

## 1. 背景与现状调研 (Background & Current State)

`LinuxWebTool` 提供了基于 Web 的 Linux/Windows 服务器运维面板。其中“文件管理”页面（路由 `/files`，前端组件 `FilesView.js`，后端控制器 `FilesController.cs`）承担着服务器文件系统的在线浏览、文本编辑和文件管理职能。

在当前版本中，用户反馈并发现：**文件管理页面没有任何下载功能**，且无法对指定文件夹进行打包下载。

### 1.1 现状代码审计

1. **前端页面 (`src/LinuxWebTool.WebHost/wwwroot/app/views/FilesView.js`)**：
   - 现存操作仅包含：目录跳转浏览、在线文本编辑（`openEditor` / `saveEditor`）、新建文件夹（`openMkdir` / `createMkdir`）、重命名（`renameEntry` / `saveRename`）、删除文件/文件夹（`removeEntry`）、上传单文件（`triggerUpload` / `onUpload`）以及在终端中打开当前路径（`openTerminal`）。
   - 表格操作列（`<td>`）中仅有三个按钮：`在终端打开`（仅目录）、`重命名`、`删除`。
   - 顶部工具栏仅有：`在此打开终端`、`＋ 新建文件夹`、`⬆ 上传`。
   - 当用户在列表中点击一个非文本文件（如 `.tar.gz`、`.zip`、`.mp4`、`.bin`）时，系统仅仅弹出一条提示：`该文件非文本，暂不支持在线编辑`，没有任何下载引导或触发动作。

2. **前端网络层与配置 (`config.js` / `client.js`)**：
   - `config.js` 中的 `API.files` 节点仅配置了 `list`、`content`、`mkdir`、`rename`、`remove`、`upload`，未定义 `download` 相关的端点。
   - 值得注意的是：**前端底层网络库 `src/LinuxWebTool.WebHost/wwwroot/app/api/client.js` 中早已封装了高水准的 `httpDownload` 函数**（第 175~230 行），它已具备：
     - 自动附加管理员 JWT Bearer 鉴权凭据与 API Key 支持；
     - 自动检测并刷新 Token（`x-renewed-token`）；
     - 401 统一重定向到登录页与网络错误 Toast 提示；
     - 自动解析 RFC 5987 / RFC 6266 标准的 `Content-Disposition: attachment; filename=...; filename*=UTF-8''...` 响应头以准确提取原始文件名（支持中文与特殊字符）；
     - 自动创建不可见的 `<a>` 标签调用 `URL.createObjectURL(blob)` 触发浏览器原生下载，并在完成后通过 `URL.revokeObjectURL` 释放内存。
     - 此工具函数在转码预设导出（`TranscodeView.js` 中的 `httpDownload(API.transcode.presetExport)`）已得到充分验证，但文件管理模块从未接入。

3. **后端接口 (`src/LinuxWebTool.WebHost/Features/Files/Routes/FilesController.cs`)**：
   - 现存 Action 仅有：`List`、`ReadContent`、`WriteContent`、`Mkdir`、`Rename`、`Delete`、`Upload`。
   - **关键历史线索**：在 `FilesController.cs` 第 20~21 行明确写有注释：
     ```csharp
     /// <summary>文本查看 / 编辑上限（2MB），超限提示下载而不是误读大文件。</summary>
     private const long MaxTextBytes = 2 * 1024 * 1024;
     ```
     并在第 117 行超限报错时返回：
     ```csharp
     return BadRequest(new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false));
     ```
     这证实了：**在项目早期架构设计中已经规划了“超限应走下载”的业务逻辑，但后续因优先迭代其他模块，下载端点一直未曾具体实现，处于 TODO 悬挂状态**。

4. **架构门禁与契约体系**：
   - 项目受到极其严格的架构门禁管控（如 `LinuxArch008` DTO 规范、`LinuxArch009` / `LinuxArch012` Minimal API 映射一致性、`LinuxArch024` API Key 权限覆盖、`FE-API-METHOD` 前后端动词与路径契约一致性等）。
   - 新增端点必须同步在 `MinimalApi/EndpointsMapper.g.cs` 注册并在 `config.js` 声明，且测试全绿通过。

---

## 2. 问题陈述 (Problem Statement)

1. **单文件下载缺失**：用户在服务器上生成的日志文件、备份文件、数据库导出的 `.db` / `.sql`、以及超过 2MB 的各类文本或二进制文件，无法通过 Web 界面下载到本地计算机。
2. **文件夹下载缺失**：无法直接从 Web 界面下载目录。用户若需要获取整个项目的源代码目录、配置目录或日志文件夹，必须先登录终端通过 `tar`/`zip` 命令手动打包，再通过 SFTP 或其它第三方工具拉取，破坏了管理面板的自闭环体验。
3. **前端交互断层**：非文本文件点击后提示“暂不支持在线编辑”，用户陷入“既无法在线看，又无法下载回来看”的死胡同。

---

## 3. 原因分析 (Root Causes)

1. **早期产品定位与 MVP 边界割裂**：
   - 系统早期重点关注在“Linux 服务器远程轻量运维”，核心场景包括在 Web 页面直接查看/编辑配置文件（如 `.conf`、`.yaml`、`.sh`）以及终端执行。下载文件属于较重度的流式数据传输，在 MVP 阶段被优先级延后。
2. **未闭环的遗留代码（TODO 状态）**：
   - 控制器源码注释已显式预留了“超限提示下载”的意图，但后续迭代重心转移至 SMB 挂载状态机、FFmpeg 媒体转码、YARP 智能网关与 EasyTier 组网等复杂模块，导致文件管理基础下载接口被遗漏。
3. **文件夹下载的技术与安全复杂度考量**：
   - 文件夹不是单个物理文件，浏览器原生无法直接以“HTTP GET 目录”的方式下载；
   - 若做文件夹打包下载，面临诸多工程与安全挑战：
     - **内存开销**：如果在内存（如 `MemoryStream`）中一次性构建 zip 包，大文件夹会导致内存暴涨甚至 OOM（严重破坏低配 VPS/容器稳定性）；
     - **软链接与循环引用**：Linux 目录下常有指向父级目录或根目录的软链接（Symbolic Link / Reparse Point），盲目递归遍历会导致无限死循环与全盘泄露；
     - **敏感目录安全越权**：若允许对根目录 `/` 或程序自身数据库/配置目录（`DataPaths.Root`）打包，将造成严重信息泄露与性能瘫痪；
     - **临时文件清理**：若在磁盘生成 zip 文件，网络中断或下载完成后的临时文件清理若不可靠，会导致磁盘空间被垃圾文件逐步耗尽。
   - 由于缺乏成体系的设计方案，导致文件夹下载功能一直搁置。

---

## 4. 文件夹下载可行性调研 (Folder Download Feasibility & Research)

### 4.1 可行性结论

**答案：完全可行，并且推荐一并支持！**
利用 .NET BCL 内置的 `System.IO.Compression.ZipArchive`，无需引入任何第三方包，完全符合 Native AOT 纯净性与零外部依赖要求。

### 4.2 技术方案选型与对比

| 方案维度 | 方案 A：纯前端 JSZip 递归下载打包 | 方案 B：后端直接向 HTTP 响应流写 Zip (Chunked) | 方案 C：后端受控临时文件打包 + `FileOptions.DeleteOnClose` 流式输出 (推荐) |
|---|---|---|---|
| **网络开销** | 极高（需前端发起数百/数千次小文件 HTTP 请求） | 低（单次 HTTP GET 请求） | 低（单次 HTTP GET 请求） |
| **内存与性能** | 极高（浏览器直接持有全部文件内存，极易卡死崩溃） | 最优（边读边压边发） | 优（磁盘流式写入与读取，内存占用极低） |
| **错误处理能力** | 差（任何一个文件 403/404 导致整体逻辑混乱） | **极差**（HTTP 200 已发出后若中途读文件失败，客户端只会收到损坏的断头 Zip） | **极优**（打包完成后再发送 HTTP 200；打包中途出错可向客户端返回标准 HTTP 400/500 JSON 错误信息） |
| **下载进度显示** | 需前端自行维护百分比 | 无法预知 `Content-Length`，浏览器无法显示总大小与进度条 | **具备确切 `Content-Length`**，浏览器原生呈现完整进度条与预估时间 |
| **实施复杂度** | 中等，且增加前端包袱 | 较高，异常恢复复杂 | 结构清晰，异常隔离可靠，操作系统自动负责文件销毁 |

综合对比，**方案 C 具备最高的生产稳定性、最优的错误处理能力与最佳的浏览器原生体验**。

### 4.3 核心工程设计与安全防范

1. **自动生命周期与防磁盘泄漏**：
   - 打包生成唯一的临时文件：`Path.Combine(Path.GetTempPath(), $"lwt_dl_{Guid.NewGuid():N}.zip")`；
   - 打包成功后，使用 `new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose)` 打开；
   - 通过 ASP.NET Core `Results.File(stream, "application/zip", $"{folderName}.zip")` 返回给客户端；
   - 当客户端下载完成、断开连接或取消时，流被 Dispose，操作系统底层自动安全删除该临时文件，零磁盘残留。若打包中途发生异常，`finally` 块中显式执行 `File.Delete`。

2. **多重安全防护机制**：
   - **路径规范化**：调用 `FileBrowserPath.Normalize` 防御 `../` 路径穿越；
   - **根目录保护**：严禁下载根目录（`FileBrowserPath.IsRoot(normalized)`），避免扫描全盘；
   - **数据目录保护**：严禁下载系统数据目录（`IsProtected(normalized)`）；
   - **软链接防循环**：递归遍历时检查 `(dir.Attributes & FileAttributes.ReparsePoint) != 0`，跳过所有符号链接/挂载交差点，从根本上杜绝死循环；
   - **容量与数量熔断**：限制打包最大文件数（例如 20,000 个）与最大未压缩总容量（例如 2GB），防止恶意请求导致服务拒绝（DoS）；
   - **文件名编码**：`ZipArchive` 使用 `Encoding.UTF8`，保留文件修改时间与完整相对目录层级（包含空目录）；
   - **并发只读安全**：读取源文件时使用 `FileShare.ReadWrite | FileShare.Delete`，允许正在被日志进程写入的文件顺利打包，不发生文件占用冲突。

---

## 5. 解决方案架构设计 (Solution Architecture)

### 5.1 后端：统一智能下载端点 `GET /api/Files/Download`

将单文件下载与文件夹打包下载统一整合为一个端点 `GET /api/Files/Download?path=...`，由后端根据目标类型自动分流：
- **目标为普通文件**：
  - 调用 `PhysicalFile(normalized, "application/octet-stream", fileName)`；
  - 享受 ASP.NET Core 原生内核级零拷贝与 HTTP Range 断点续传支持；
  - 记录审计日志：`下载文件`。
- **目标为目录**：
  - 执行受控临时 Zip 打包流程；
  - 输出 `Content-Type: application/zip`，下载文件名设为 `{folderName}.zip`；
  - 记录审计日志：`打包下载文件夹`。

### 5.2 契约与路由注册

1. **Minimal API 映射 (`EndpointsMapper.g.cs`)**：
   ```csharp
   group_FilesController.MapGet("Download", async ([FromServices] FilesController ctrl, HttpContext ctx, [FromQuery] string path) => { ctrl.HttpContext = ctx; return await ctrl.Download(path); });
   ```
2. **API 客户端配置 (`config.js`)**：
   ```javascript
   files: {
     list: '/Files',
     content: '/Files/Content',
     download: '/Files/Download',
     mkdir: '/Files/Mkdir',
     rename: '/Files/Rename',
     remove: () => '/Files',
     upload: () => '/Files/Upload',
   }
   ```

### 5.3 前端交互设计与优化 (`FilesView.js`)

1. **表格操作列按钮**：
   - 增加「下载」操作：
     - 若为文件夹：展示 `打包下载`；
     - 若为普通文件：展示 `下载`；
   - 支持下载中的 Loading 状态，防止用户快速重复点击；
2. **顶部工具栏**：
   - 增加「📦 打包当前目录」快捷按钮，支持一键打包当前正处于的文件夹；
3. **直观交互改进**：
   - 用户在列表中点击非文本文件时，直接触发下载；
   - 在文本编辑弹窗（`showEditor`）的右上角增加「⬇ 下载」按钮，方便在查看/编辑文本时随时保存到本地。

---

## 6. 实施与验证清单 (Execution & Verification Plan)

- [x] **撰写并归档调研与落地方案**：写入 `docs/file-manager-download-and-folder-zip-plan.md`。
- [x] **后端实现**：在 `FilesController.cs` 中实现 `Download` 方法，支持单文件与目录 Zip 打包。
- [x] **路由注册**：在 `EndpointsMapper.g.cs` 注册映射并确保 `LinuxArch012` 架构门禁通过。
- [x] **前端实现**：更新 `config.js` 与 `FilesView.js`，引入 `httpDownload` 并完善交互。
- [x] **集成测试**：在 `LinuxWebTool.IntegrationTests` 补充单文件与文件夹 Zip 下载的自动化用例。
- [x] **门禁全量验证**：执行 `./scripts/verify-fast.ps1`，验证编译、架构测试、集成测试与前端 lint 100% 通过。
