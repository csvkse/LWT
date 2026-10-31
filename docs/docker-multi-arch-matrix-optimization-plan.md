# GitHub Actions Docker 多架构构建矩阵收敛优化 (方案 1)

## 1. 背景与瓶颈分析

在原先的 Docker CI/CD 流水线（`.github/workflows/ci.yml`）中，镜像构建任务由两个维度做正交笛卡尔积生成：
- **CPU 架构（2 个）**：`linux/amd64` 与 `linux/arm64`；
- **GPU 驱动变体（4 个）**：`all`、`intel`、`amd`、`nv`；
- **总任务数**：$2 \times 4 = 8$ 个 Runner 并行构建。

### 存在的问题与冗余
1. **ARM64 重复无效构建**：在 `Dockerfile` 中，Intel 驱动仅在 x86_64 存在，ARM64 物理机自动跳过。因此在 ARM64 上，`intel` 与 `nv` 完全相同（未安装任何驱动），`all` 与 `amd` 完全相同（仅安装 Mesa Gallium）。4 个 ARM64 Job 存在一半以上的纯算力浪费。
2. **Native AOT 编译严重重复**：每个 Job 都独立拉取容器完整编译 `.NET Native AOT`，8 个 Job 重复跑了 8 次 C++ 链接与前端打包。
3. **Playwright 浏览器测试重复**：在 `amd64` 上启动无头 Chromium 跑了 4 次相同的端到端冒烟测试。
4. **配额与并发压力**：一次性占用 8 个 GitHub Actions Runner（含高成本的 ARM64 原生物理机），大幅挤占并发配额，耗时常达 10~15 分钟。

---

## 2. 方案 1 优化设计：双架构统一镜像 + 标签别名（Tag Alias）

### 2.1 架构设计
- **收敛构建矩阵**：
  - `arch: amd64` (ubuntu-latest)：全能集成 Intel iHD + Mesa Gallium + NVENC 透传支持；
  - `arch: arm64` (ubuntu-24.04-arm)：全能集成 Mesa Gallium + NVENC 透传支持；
  - 构建矩阵由 **8 个缩减为 2 个**。
- **发布多架构 Manifest 与标签别名**：
  在 `manifest` 阶段，通过 `docker buildx imagetools create` 将已构建好的 `amd64` 与 `arm64` 单层合并，并同时注入所有兼容标签别名：
  - 核心标签：`:latest`、`:vX.Y.Z`、`:sha-{sha}`
  - 兼容别名：`:latest-intel`、`:latest-amd`、`:latest-nv`、`:latest-all`（以及对应版本号后缀标签）。

### 2.2 收益对比
| 维度 | 优化前 (8 并行) | 优化后 (方案 1) | 提升效果 |
| :--- | :---: | :---: | :---: |
| **构建 Runner 数量** | 8 个 (4 amd64 + 4 arm64) | **2 个 (1 amd64 + 1 arm64)** | **降低 75%** |
| **Native AOT 重复编译** | 8 次 | **2 次** | **算力消耗降为 1/4** |
| **Playwright 冒烟测试** | 4 次 | **1 次** | **提速 75%** |
| **总发布时长** | 10 ~ 15 分钟 | **约 3 ~ 4 分钟** | **提速 300%** |
| **下游用户体验** | 需费心辨别显卡标签 | **任意标签皆全能支持** | **零心智负担** |
