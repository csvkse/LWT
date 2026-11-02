# 原生受控常驻微服务示例 (Native Managed Demo)

本示例展示如何利用可选 HTTP 管理控制协议 (Resident Control Protocol) 实现宿主对原生二进制程序的深度控制与就绪性探测。

## 协议端点
- `POST /control/handshake`：握手并校验 `protocolVersion` 与 `runId`；
- `GET /control/health`：返回服务就绪态 (`Ready` / `Degraded`)；
- `POST /control/invoke/{action}`：触发服务内部特定动作并返回结构化数据；
- `POST /control/shutdown`：下发优雅关闭通知，配合宿主完成资源排空。
