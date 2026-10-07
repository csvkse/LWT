---
created: 2026-10-07
updated: 2026-10-07
---

# LinuxWebTool 物理目录规范化重构具体实施方案

> **指导原则**：对齐 `SoftwareArchitecture/backend`（尤其是 `AITool` 与 `InfiniWeb`）物理目录架构规范。
> **核心标准**：
> 1. 项目根级只允许：`Features/`、`Composition/`、`Shared/`、`Platform/`、`Serialization/` 以及必要的宿主入口（如 `Program.cs`、`MinimalApi/`）；
> 2. 项目根级禁止直接平铺职责名（如 `Routes/`、`Persistence/`、`Models/`、`Interfaces/`）或业务名（如 `Mount/`、`Transcode/`）；
> 3. `Features/` 下必须按具体业务所有者组织，标准职责子目录采用：`Contracts/`、`Application/`、`Persistence/`、`Platform/`、`Integration/`、`Routes/`、`Endpoints/`；
> 4. 命名空间严格镜像物理路径（符合 `LinuxArch007`）。

---

## 1. 各层物理目录重构映射表

### 1.1 `LinuxWebTool.Contracts` 迁移映射

| 原物理路径 | 目标物理路径 | 目标命名空间 |
|---|---|---|
| `Models/ResultModels.cs` | `Shared/Contracts/ResultModels.cs` | `LinuxWebTool.Contracts.Shared.Contracts` |
| `Models/RequestModels.cs` | `Shared/Contracts/RequestModels.cs` | `LinuxWebTool.Contracts.Shared.Contracts` |
| `Models/ExecutionModels.cs` | `Features/Commands/Contracts/ExecutionModels.cs` | `LinuxWebTool.Contracts.Features.Commands.Contracts` |
| `Models/ApiKeyModels.cs` | `Features/Security/Contracts/ApiKeyModels.cs` | `LinuxWebTool.Contracts.Features.Security.Contracts` |
| `Models/EasyTierNodeModels.cs` | `Features/EasyTier/Contracts/EasyTierNodeModels.cs` | `LinuxWebTool.Contracts.Features.EasyTier.Contracts` |
| `Interfaces/IEasyTierManager.cs` | `Features/EasyTier/Contracts/IEasyTierManager.cs` | `LinuxWebTool.Contracts.Features.EasyTier.Contracts` |
| `Models/FrpTunnelModels.cs` | `Features/Tunnel/Contracts/FrpTunnelModels.cs` | `LinuxWebTool.Contracts.Features.Tunnel.Contracts` |
| `Models/GatewayModels.cs` | `Features/Gateway/Contracts/GatewayModels.cs` | `LinuxWebTool.Contracts.Features.Gateway.Contracts` |
| `Models/McpModels.cs` | `Features/Mcp/Contracts/McpModels.cs` | `LinuxWebTool.Contracts.Features.Mcp.Contracts` |
| `Models/MountHealthModels.cs` | `Features/Mount/Contracts/MountHealthModels.cs` | `LinuxWebTool.Contracts.Features.Mount.Contracts` |
| `Models/MountRuntimeModels.cs` | `Features/Mount/Contracts/MountRuntimeModels.cs` | `LinuxWebTool.Contracts.Features.Mount.Contracts` |
| `Models/SmbMountModels.cs` | `Features/Mount/Contracts/SmbMountModels.cs` | `LinuxWebTool.Contracts.Features.Mount.Contracts` |
| `Models/SystemStatusModels.cs` | `Features/SystemStatus/Contracts/SystemStatusModels.cs` | `LinuxWebTool.Contracts.Features.SystemStatus.Contracts` |
| `Interfaces/ISystemStatusProvider.cs` | `Features/SystemStatus/Contracts/ISystemStatusProvider.cs` | `LinuxWebTool.Contracts.Features.SystemStatus.Contracts` |
| `Terminal/PtyContracts.cs` | `Features/Terminal/Contracts/PtyContracts.cs` | `LinuxWebTool.Contracts.Features.Terminal.Contracts` |
| `Models/TranscodeModels.cs` | `Features/Transcode/Contracts/TranscodeModels.cs` | `LinuxWebTool.Contracts.Features.Transcode.Contracts` |
| `Interfaces/IShellExecutor.cs` | `Features/Shell/Contracts/IShellExecutor.cs` | `LinuxWebTool.Contracts.Features.Shell.Contracts` |

### 1.2 `LinuxWebTool.Application` 结构（已合规并持续扩充）
- `Features/Commands/CommandWorkflowService.cs`
- `Features/Mount/MountHealthEvaluator.cs`
- `Features/Security/ApiKeyPermissionMatrix.cs`
- `Features/Transcode/TranscodeJobPlanner.cs`

### 1.3 `LinuxWebTool.WebHost` 迁移映射

| 原物理路径 | 目标物理路径 | 目标命名空间 |
|---|---|---|
| `Routes/AuthController.cs` | `Features/Auth/Routes/AuthController.cs` | `LinuxWebTool.WebHost.Features.Auth.Routes` |
| `Routes/CommandsController.cs` | `Features/Commands/Routes/CommandsController.cs` | `LinuxWebTool.WebHost.Features.Commands.Routes` |
| `Routes/GroupsController.cs` | `Features/Commands/Routes/GroupsController.cs` | `LinuxWebTool.WebHost.Features.Commands.Routes` |
| `Routes/HistoryController.cs` | `Features/Commands/Routes/HistoryController.cs` | `LinuxWebTool.WebHost.Features.Commands.Routes` |
| `Routes/EasyTierController.cs` | `Features/EasyTier/Routes/EasyTierController.cs` | `LinuxWebTool.WebHost.Features.EasyTier.Routes` |
| `Routes/FilesController.cs` | `Features/Files/Routes/FilesController.cs` | `LinuxWebTool.WebHost.Features.Files.Routes` |
| `Routes/FrpTunnelController.cs` | `Features/Tunnel/Routes/FrpTunnelController.cs` | `LinuxWebTool.WebHost.Features.Tunnel.Routes` |
| `Routes/GatewayController.cs` | `Features/Gateway/Routes/GatewayController.cs` | `LinuxWebTool.WebHost.Features.Gateway.Routes` |
| `Routes/LogsController.cs` | `Features/Logging/Routes/LogsController.cs` | `LinuxWebTool.WebHost.Features.Logging.Routes` |
| `Routes/MountTasksController.cs` | `Features/Mount/Routes/MountTasksController.cs` | `LinuxWebTool.WebHost.Features.Mount.Routes` |
| `Routes/SmbMountsController.cs` | `Features/Mount/Routes/SmbMountsController.cs` | `LinuxWebTool.WebHost.Features.Mount.Routes` |
| `Routes/WebDavMountsController.cs` | `Features/Mount/Routes/WebDavMountsController.cs` | `LinuxWebTool.WebHost.Features.Mount.Routes` |
| `Routes/RcloneMountsController.cs` | `Features/Mount/Routes/RcloneMountsController.cs` | `LinuxWebTool.WebHost.Features.Mount.Routes` |
| `Routes/OverviewController.cs` | `Features/SystemStatus/Routes/OverviewController.cs` | `LinuxWebTool.WebHost.Features.SystemStatus.Routes` |
| `Routes/SystemStatusController.cs` | `Features/SystemStatus/Routes/SystemStatusController.cs` | `LinuxWebTool.WebHost.Features.SystemStatus.Routes` |
| `Routes/SchedulesController.cs` | `Features/Schedule/Routes/SchedulesController.cs` | `LinuxWebTool.WebHost.Features.Schedule.Routes` |
| `Routes/TerminalController.cs` | `Features/Terminal/Routes/TerminalController.cs` | `LinuxWebTool.WebHost.Features.Terminal.Routes` |
| `Routes/TranscodeController.cs` | `Features/Transcode/Routes/TranscodeController.cs` | `LinuxWebTool.WebHost.Features.Transcode.Routes` |
| `Routes/ApiKeysController.cs` | `Features/Security/Routes/ApiKeysController.cs` | `LinuxWebTool.WebHost.Features.Security.Routes` |
| `Routes/ApiResponses.cs` | `Shared/Routes/ApiResponses.cs` | `LinuxWebTool.WebHost.Shared.Routes` |
| `Endpoints/TerminalEndpoints.cs` | `Features/Terminal/Endpoints/TerminalEndpoints.cs` | `LinuxWebTool.WebHost.Features.Terminal.Endpoints` |
| `Endpoints/McpEndpoints.cs` | `Features/Mcp/Endpoints/McpEndpoints.cs` | `LinuxWebTool.WebHost.Features.Mcp.Endpoints` |
| `Mcp/McpServerEngine.cs` | `Features/Mcp/Engine/McpServerEngine.cs` | `LinuxWebTool.WebHost.Features.Mcp.Engine` |
| `Gateway/*` | `Features/Gateway/Adapters/*` | `LinuxWebTool.WebHost.Features.Gateway.Adapters` |
| `Middleware/*` | `Shared/Middleware/*` | `LinuxWebTool.WebHost.Shared.Middleware` |
| `Extensions/*` | `Shared/Extensions/*` | `LinuxWebTool.WebHost.Shared.Extensions` |

### 1.4 `LinuxWebTool.Infrastructure` 迁移映射

| 原物理路径 | 目标物理路径 | 目标命名空间 |
|---|---|---|
| `Persistence/` | `Shared/Persistence/` | `LinuxWebTool.Infrastructure.Shared.Persistence` |
| `Support/` | `Shared/Support/` | `LinuxWebTool.Infrastructure.Shared.Support` |
| `Logging/` | `Features/Logging/Adapters/` | `LinuxWebTool.Infrastructure.Features.Logging.Adapters` |
| `EasyTier/` | `Features/EasyTier/Adapters/` | `LinuxWebTool.Infrastructure.Features.EasyTier.Adapters` |
| `Gateway/` | `Features/Gateway/Adapters/` | `LinuxWebTool.Infrastructure.Features.Gateway.Adapters` |
| `Mount/` | `Features/Mount/Adapters/` | `LinuxWebTool.Infrastructure.Features.Mount.Adapters` |
| `Scheduling/` | `Features/Scheduling/Adapters/` | `LinuxWebTool.Infrastructure.Features.Scheduling.Adapters` |
| `Security/` | `Features/Security/Adapters/` | `LinuxWebTool.Infrastructure.Features.Security.Adapters` |
| `Shell/` | `Features/Shell/Adapters/` | `LinuxWebTool.Infrastructure.Features.Shell.Adapters` |
| `SystemInfo/` | `Features/SystemInfo/Platform/` | `LinuxWebTool.Infrastructure.Features.SystemInfo.Platform` |
| `Terminal/` | `Features/Terminal/Platform/` | `LinuxWebTool.Infrastructure.Features.Terminal.Platform` |
| `Transcode/` | `Features/Transcode/Adapters/` | `LinuxWebTool.Infrastructure.Features.Transcode.Adapters` |
| `Tunnel/` | `Features/Tunnel/Adapters/` | `LinuxWebTool.Infrastructure.Features.Tunnel.Adapters` |

---

## 2. 架构门禁适配与升级 (Architecture Gates Refactoring)

为支撑物理目录平滑演进并消除硬编码路径，需对 `ArchitectureTests.cs` 进行如下关键解耦升级：
1. **解耦 Controller 扫描**：将 `LinuxArch005`、`008`、`012`、`017` 从单一定位 `src/LinuxWebTool.WebHost/Routes` 升级为**全工程递归定位所有 `*Controller.cs`**（无论是根级还是 `Features/**/Routes` 均可完整捕获）；
2. **解耦 Store / Persistence 扫描**：将 `LinuxArch013`、`014` 从单一路径升级为全工程递归扫描 `*Store.cs` 与 `DbSetup.cs`；
3. **新增根目录规范门禁 `LinuxArch023`**：严格断言生产项目根级只允许 `Features`、`Composition`、`Shared`、`MinimalApi` 等白名单目录。

---

## 3. 落地实施执行顺序

1. **阶段 1：门禁解耦先行**：在 `ArchitectureTests.cs` 中实现控制器与持久化文件的动态多路径发现，保证现有结构与重构中结构同时被严格约束；
2. **阶段 2：Contracts 规范化**：按 Feature 迁移 17 个文件，建立 `LinuxWebTool.Contracts` 全局命名空间导出以保持跨项目兼容；
3. **阶段 3：WebHost 规范化**：按 Feature 迁移控制器、端点与引擎文件，重新生成/对齐 `EndpointsMapper.g.cs`；
4. **阶段 4：Infrastructure 规范化**：将 115 个文件按 Feature 归入 `Features/<Feature>/` 与 `Shared/`；
5. **阶段 5：全量门禁闭环**：执行 `./scripts/verify-fast.ps1` 进行全量构建、架构门禁测试与集成测试闭环。
