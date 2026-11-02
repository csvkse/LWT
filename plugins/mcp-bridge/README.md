# MCP Bridge Worker

Go 单进程 OpenAI Secure MCP Tunnel 与第三方 MCP 工具纳管服务。

## 架构

```mermaid
flowchart LR
    CP["OpenAI Control Plane\n(ChatGPT / Responses API)"] <-->|Long-Poll / Response| TC["Tunnel Client Runtime\n(Official SDK)"]
    TC <-->|In-Memory Transport| Facade["MCP Facade Server"]
    Facade --> Gateway["Admission & Routing Gateway"]
    Gateway --> Mgr["Backend Connection Manager"]
    Mgr --> T1["stdio (Subprocess)"]
    Mgr --> T2["Streamable HTTP MCP"]
    Mgr --> T3["HTTP+SSE MCP"]
    Host["LinuxWebTool C# Host"] <-->|Resident Control\n/control/* (Loopback)| Admin["Admin Control Handler"]
```

## 特性

- **官方 SDK 驱动**: 集成 `github.com/openai/tunnel-client` 与 `github.com/modelcontextprotocol/go-sdk`。
- **纯粹单进程与单所有者**: 进程文件锁 `owner.lock` 与临时 token 文件防重放及多实例冲突。
- **三传输协议全面支持**: 本地 `stdio` 子进程、`streamable-http` 及 `sse`。
- **动态审批与显式允许**: 新工具默认关闭，仅暴露管理员明确复核勾选的工具，上游 Schema 变更自动拦截待审。
- **有界日志与审计**: 16 MiB 滚动持久审计，元数据脱敏，不泄漏任何参数与凭据。
