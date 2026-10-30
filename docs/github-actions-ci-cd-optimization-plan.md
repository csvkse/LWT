# GitHub Actions CI/CD 分工解耦与双重构建优化方案 (方案 1)

## 1. 背景与问题定义
- 在此前的 CI/CD 流程中，每次向 GitHub 推送版本（`git push origin main vX.Y.Z`）时，GitHub Actions 会分别触发分支推送与标签推送两个事件。
- 两个事件独立启动各自的 `CI` 工作流实例，每个实例均包含 1 个门禁任务（`build-test`）、8 个跨平台跨架构 GPU 变体 Docker 编译矩阵任务（`docker-build`）以及 1 个多架构 Manifest 发布任务（`manifest`），总计并发启动 20 个 Job。
- 由于 `main` 和 `vX.Y.Z` 指向相同的 Git Commit SHA，且标签发布步骤本身就会同时推送 `:vX.Y.Z` 和 `:latest` 镜像，因此 `main` 分支执行的 8 矩阵 Docker 镜像编译构建属于 100% 算力与配额浪费，并相互争抢并发 Runner 资源。

---

## 2. 方案 1 优化设计（CI 门禁与 Release 发布分工解耦）

### 2.1 职责分工模型
```
Git 事件: push to main / PR
  │
  ▼
[CI 工作流]
  │
  ├─► Build & Gates (架构测试 182 项 + 单元测试 159 项 + 前端门禁)  --> [~1.5 分钟完成反馈]
  │
  └─► (自动跳过 8 矩阵 Docker 镜像编译与 Manifest 发布)

─────────────────────────────────────────────────────────────────────────────

Git 事件: push tag (v*) / 手动调度
  │
  ▼
[CI 工作流]
  │
  ├─► Build & Gates (全量门禁检验，确保发布无回归)
  │
  ├─► Docker Image Matrix (8 个平台与 GPU 变体编译)
  │
  └─► Publish Multi-Arch Manifests (同时发布 :vX.Y.Z 与 :latest 镜像)

[Desktop Release 工作流] (并发执行)
  │
  └─► Windows / Linux x64 / Linux ARM64 桌面端自包含二进制打包
```

### 2.2 核心优化收益
1. **日常开发体验跃升**：
   - 开发者平时向 `main` 分支提代码或合并 PR 时，CI 门禁在 **1.5 ~ 2 分钟** 内即可给出绿色通过反馈，彻底告别 18 分钟的漫长等待。
2. **发布时零重复与并发自由**：
   - 当同时推送 `main` 和版本标签时，`main` 的 CI 在 1.5 分钟内执行完毕，将所有 GitHub Actions Runner（尤其是稀缺的原生 ARM64 物理机配额）全部让给标签构建，缩短正式发版总耗时。
3. **保持 `:latest` 镜像自动滚动更新**：
   - 每次打版本标签发布时，生成的 Docker 镜像会同时被打上 `:vX.Y.Z` 与 `:latest`（及各自的厂商后缀），既能满足生产环境锁定版本号的诉求，也保障了拉取最新镜像用户的更新需求。
4. **Native AOT 独立性优化**：
   - 将 `aot.yml` 限定为仅在 `main` 分支推送与 PR 时触发单容器烟雾验证，打版本标签时不重复触发，进一步释放 Runner。
