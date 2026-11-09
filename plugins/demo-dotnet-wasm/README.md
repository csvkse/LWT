# C# WASI 极速微函数示例 (demo-dotnet-wasm)

本示例展示如何使用 **C# 14 (.NET 10)** 编写并编译符合 WebAssembly System Interface (WASI) 规范的纯沙箱微函数。

---

## 1. 架构特性

- **纯 WebAssembly 字节码**：输出为独立的 `.wasm` 二进制，脱离浏览器与 DOM 依赖；
- **冷启动 < 5ms**：直接由 `wasmtime` 或通用的 Node.js WASI 宿主以纳秒级速度即时装载；
- **内核级内存隔离**：基于线性内存空间与 capability-based 安全模型，不具备越权读取宿主内存或未授权目录的能力；
- **零常驻开销**：不监听任何 TCP 端口，不消耗长期内存，执行完毕立即释放。

---

## 2. 编译为 WASI 字节码

在具备 .NET 10 SDK 的开发机器上执行：

```bash
# 1. 安装 WASI 实验性工作负载 (若未安装)
dotnet workload install wasi-experimental

# 2. 编译为 wasi-wasm 二进制
dotnet build -c Release -r wasi-wasm

# 3. 产物复制到当前目录
cp bin/Release/net10.0/wasi-wasm/AppBundle/demo-dotnet-wasm.wasm ./demo-dotnet-wasm.wasm
```

---

## 3. 本地调试与运行

你可以使用 `wasmtime` 直接调试该微函数：

```bash
# 运行默认函数
wasmtime run demo-dotnet-wasm.wasm

# 传递 Action 与参数
wasmtime run --env LWT_ACTION=calc --env LWT_PARAM=10 demo-dotnet-wasm.wasm
```
