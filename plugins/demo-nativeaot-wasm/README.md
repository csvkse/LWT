# C# NativeAOT-LLVM 极速微函数示例 (demo-nativeaot-wasm)

本示例展示如何使用 **C# NativeAOT-LLVM (如 `bflat`)** 构建无解释器、纯单一静态二进制的纳秒级 WebAssembly 微函数。

---

## 1. 核心架构与优势

- **极致体积**：纯二进制产物仅需 300KB ~ 1MB（相比传统 Blazor Mono 动辄 20MB+ 缩减 95%）；
- **纳秒/毫秒级冷启动**：< 2ms 即时装载执行，零后台常驻守护进程，零系统端口占用；
- **纯单一文件 (Single-File Wasm)**：脱离所有 `.dll` 依赖，直接由 LLVM 生成纯静态字节码；
- **在线源码编辑与即时编译支持**：可在平台 Web 界面直接编辑 `Program.cs`，调用编译器后端实现即改即生效。

---

## 2. 编译为 NativeAOT-LLVM Wasm 字节码

### 方案 A：使用 `bflat` 工具链一键编译（推荐，速度极快）
`bflat` 是 Michal Strehovsky 开发的开箱即用 C# Native AOT 编译器，内置 LLVM 代码生成器：

```bash
# 安装 bflat (Linux)
curl -sSL https://github.com/bflattened/bflat/releases/latest/download/bflat-x64-linux.tar.gz | tar -xz -C /usr/local/bin

# 将 Program.cs 编译为独立 Wasm 二进制
bflat build --target:wasm -o demo-nativeaot-wasm.wasm Program.cs
```

### 方案 B：使用 .NET 10 官方 WASI SDK
```bash
dotnet build -c Release -r wasi-wasm
cp bin/Release/net10.0/wasi-wasm/AppBundle/demo-nativeaot-wasm.wasm ./demo-nativeaot-wasm.wasm
```

---

## 3. 本地调试与运行

```bash
# 1. 默认运行
wasmtime run demo-nativeaot-wasm.wasm

# 2. 触发斐波那契高并发计算动作
wasmtime run --env LWT_ACTION=fib --env LWT_PARAM=20 demo-nativeaot-wasm.wasm

# 3. 触发平方运算
wasmtime run --env LWT_ACTION=calc --env LWT_PARAM=16 demo-nativeaot-wasm.wasm
```
