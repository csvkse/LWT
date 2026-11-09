# Node.js Serverless 动态按需 API 示例 (demo-serverless-api)

本示例展示基于 LinuxWebTool `PluginProfile.Serverless` 规范的无服务器微函数执行模型。

---

## 1. 架构特性

- **按需冷启动（On-Demand Spawning）**：无请求到达时处于 Scale-to-Zero 缩容休眠态，零端口占用，零后台内存开销；
- **突发并发热机保持**：首次请求触发极速唤醒，并自动维持 5 分钟空闲保活计时器，在突发高频调用期保持低延迟；
- **内核配额与安全隔离**：受 Linux cgroup v2 严格约束（CPU/内存/PIDs），并经过 [`PluginEnvironmentSanitizer`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/PluginEnvironmentSanitizer.cs) 脱敏处理。
