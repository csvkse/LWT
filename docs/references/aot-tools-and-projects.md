# Native AOT 参考项目与工具记录

## 参考项目

### ProxyYARP

- 本机路径：`E:\WorkProject\C#\【个人项目】\命令行\ProxyYARP`
- 用途：参考 Native AOT WebHost 组织方式、发布配置、容器构建和源生成实践。
- 使用建议：对照项目文件、Dockerfile、JSON source-generation context 和启动流程；不要直接复制业务代码或路径假设。

### XrayMGR

- 本机路径：`E:\WorkProject\C#\【个人项目】\命令行\XrayMGR`
- 用途：参考 AOT 迁移后的服务拆分、配置读取、接口测试和运行时兼容处理。
- 使用建议：重点比较 AOT 发布参数、依赖包版本、测试脚本和 Linux 容器运行方式。

## 本项目工具记录

| 工具/脚本 | 用途 |
| --- | --- |
| `dotnet test LinuxWebTool.slnx -c Release` | 全解决方案单元/架构/集成测试 |
| `scripts/verify-fast.ps1` | 本地快速构建、后端门禁、集成测试和前端门禁 |
| `scripts/verify-aot.ps1` | 构建 Native AOT Docker 镜像并调用容器冒烟测试 |
| `scripts/smoke-aot.ps1` | 启动临时容器、登录、验证后端路由和扫描 AOT 动态代码错误 |
| `wwwroot/frontend-gate.cjs` | 前端 API 所有权、事件绑定和模块边界检查 |
| `gh run list/view/watch` | 查看 GitHub Actions CI 与 Native AOT 运行状态 |
| Docker / Docker Desktop WSL2 | 本机 Linux 容器构建和 AOT 运行验证 |

## 记录规范

每次 AOT 相关变更应记录：提交号、使用的 .NET/依赖版本、镜像标签、测试数量、冒烟路由数量、失败日志和剩余 IL2026/IL3050 警告。参考项目只作为实现经验来源，最终判断以本项目源码、测试和容器结果为准。
