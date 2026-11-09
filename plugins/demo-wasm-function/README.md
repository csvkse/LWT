# WebAssembly 极速微函数示例 (demo-wasm-function)

本示例展示基于 WebAssembly 内存安全沙箱的纳秒级微函数执行。

---

## 1. 源码与汇编

源码使用 WebAssembly Text (`function.wat`) 编写，导出 `add`, `calc`, `run` 等函数。

### 使用 wat2wasm 工具编译
```bash
wat2wasm function.wat -o main.wasm
```

---

## 2. 运行时执行与调度

- **极速冷启动**：由系统执行策略 [`WasmMicroFunctionStrategy.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Plugins/Strategies/WasmMicroFunctionStrategy.cs) 统一调度；
- **双引擎自适应**：
  - 容器内若安装了 `wasmtime`，直接通过高性能独立引擎调用；
  - 若无独立运行时，自动无缝平滑回退至 Node.js WASI / WebAssembly 驱动；
- **参数传递**：
  - 支持传入数字或逗号分隔列表（如 `action=add&param=10,20`），直接映射为 Wasm 函数入参；
  - 统一封装为标准 JSON 格式返回。
