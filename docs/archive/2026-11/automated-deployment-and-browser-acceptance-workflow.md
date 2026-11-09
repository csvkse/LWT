# LinuxWebTool 自动化发布、Linux 部署与内置浏览器验收全流程规范

本文档详尽记录 LinuxWebTool 从本地代码验证、GitHub 跨架构 Native AOT 构建发布、Linux 宿主机特权容器自动化部署、容器环境依赖加速初始化，到内置浏览器自动化验收的完整端到端流程与规范。

---

## 1. 核心环境与安全凭据管理

为确保生产/测试环境凭据不发生泄露，所有敏感连接信息与开发主机配置统一固化在项目根目录的私有环境配置文件中，并在版本控制系统中强制忽略。

### 1.1 配置文件定位
- **配置文件路径**：`/env/.envdev`（位于项目根目录）
- **版本库忽略规则**：已在根目录 [`.gitignore`](file:///.gitignore) 中添加：
  ```gitignore
  .env
  .env.*
  env/
  .envdev
  *.envdev
  ```
  `git status` 与 `git check-ignore` 均已校验确保文件绝对不会被 Git 跟踪或提交。

### 1.2 环境配置字段清单
配置文件结构如下：
```ini
# LinuxWebTool 远程开发与自动化部署环境配置
DEPLOY_HOST=10.126.1.70
DEPLOY_PORT=45612
DEPLOY_USER=root
DEPLOY_SSH_KEY="-----BEGIN OPENSSH PRIVATE KEY-----\n...\n-----END OPENSSH PRIVATE KEY-----"

WEB_BASE_URL=http://10.126.1.70:5270/
WEB_PORT=5270
WEB_ADMIN_USER=admin
WEB_ADMIN_PASSWORD=your_secure_password_here

DOCKER_CONTAINER_NAME=linuxwebtool
DOCKER_IMAGE=ghcr.io/csvkse/lwt:latest
DOCKER_DATA_VOLUME=linuxwebtool-data
```

---

## 2. 全流程阶段图解

```mermaid
flowchart TD
    subgraph Phase1["阶段一：本地门禁与构建验证"]
        P1_1["代码变更与本地编码"] --> P1_2["运行前端架构门禁: frontend-gate.cjs"]
        P1_2 --> P1_3["运行后端测试: ArchitectureTests / UnitTests / IntegrationTests"]
        P1_3 --> P1_4["运行前端测试: node --test tests/frontend/*.test.mjs"]
    end

    subgraph Phase2["阶段二：版本发布与 GitHub Actions CI"]
        P2_1["推送代码至 main 分支"] --> P2_2["打语义化版本标签: git tag v0.2.x && git push"]
        P2_2 --> P2_3["GitHub Actions: Build & Gates (架构测试、代码覆盖率)"]
        P2_3 --> P2_4["GitHub Actions: 跨架构 Native AOT 镜像编译 (amd64 / arm64)"]
        P2_4 --> P2_5["GitHub Actions: 黑盒冒烟测试 (Playwright & API Contract)"]
        P2_5 --> P2_6["GitHub Actions: 发布多架构 Manifest 至 GHCR (latest & tag)"]
    end

    subgraph Phase3["阶段三：Linux 宿主机容器部署"]
        P3_1["读取 env/.envdev 建立 SSH 免密会话"] --> P3_2["拉取最新镜像: docker pull ghcr.io/csvkse/lwt:latest"]
        P3_2 --> P3_3["优雅停止并删除旧容器: docker rm -f linuxwebtool"]
        P3_3 --> P3_4["启动特权容器 (绑定 host 网络、挂载数据卷与宿主设备)"]
        P3_4 --> P3_5["轮询等待健康检查健康: docker ps (healthy)"]
    end

    subgraph Phase4["阶段四：容器依赖与镜像源加速配置"]
        P4_1["切换 Alpine 国内镜像源 (mirrors.ustc.edu.cn)"] --> P4_2["安装多语言运行时: nodejs, python3, dotnet8-sdk, pwsh, caddy"]
        P4_2 --> P4_3["验证运行环境版本与二进制在 PATH 中的可执行性"]
        P4_3 --> P4_4["重载插件系统，各插件状态就绪"]
    end

    subgraph Phase5["阶段五：内置浏览器自动化端到端验收"]
        P5_1["内置 Chrome DevTools 打开 Web 控制台"] --> P5_2["自动化登录并提取 JWT 凭据"]
        P5_2 --> P5_3["导航至 /app/#/plugins 采集无障碍快照 (A11y Tree)"]
        P5_3 --> P5_4["测试安装引导弹窗 (多发行版命令填入、作用域切换)"]
        P5_4 --> P5_5["触发常驻微服务启动并校验反向代理与 HTTP 200"]
        P5_5 --> P5_6["触发轻量脚本自定义动作并校验退出码与 JSON 输出"]
        P5_6 --> P5_7["全屏截图留证与生成验收报告"]
    end

    Phase1 --> Phase2 --> Phase3 --> Phase4 --> Phase5
```

---

## 3. 详细执行流程与操作命令

### 阶段一：本地门禁与构建验证
在提交任何代码前，必须确保本地架构门禁与全量单元测试通过：
```powershell
# 1. 前端架构门禁与 ESM 完整性扫描
node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs

# 2. 前端单元测试（状态机、策略过滤、暗黑主题规范）
node --test tests/frontend/*.test.mjs

# 3. 后端单元测试与架构门禁
dotnet test tests/LinuxWebTool.ArchitectureTests
dotnet test tests/LinuxWebTool.UnitTests
dotnet test tests/LinuxWebTool.IntegrationTests
```

### 阶段二：版本发布与 GitHub Actions CI
当功能经过本地严密测试后，通过 Git 标签触发自动化流水线：
```bash
# 1. 提交代码并推送主干
git add -A
git commit -m "feat(...): 描述提交功能"
git push origin main

# 2. 创建并推送语义化版本标签（触发 GHCR 镜像构建）
git tag v0.2.48
git push origin v0.2.48

# 3. 查看 Actions 运行状态
gh run list -L 3
gh run view <RUN_ID>
```
> [!NOTE]
> GitHub Actions 流水线包含 AMD64（原生 Ubuntu 虚拟机）与 ARM64（原生 64 位物理机）双环境并行构建，采用 .NET 10 Native AOT 极速编译，耗时约 7~9 分钟，完成后自动合并为单镜像多架构 Manifest 并推送到 `ghcr.io/csvkse/lwt:latest`。

### 阶段三：Linux 宿主机特权容器自动化部署
通过 SSH 连接远程宿主机，执行容器滚动更新：
```bash
# 1. 拉取最新镜像
docker pull ghcr.io/csvkse/lwt:latest

# 2. 销毁旧容器（数据卷保留，持久化数据不丢失）
docker rm -f linuxwebtool

# 3. 重新运行特权容器
docker run -d \
  --name linuxwebtool \
  --restart unless-stopped \
  --privileged \
  --pid=host \
  --user root \
  --device /dev/fuse \
  --device=/dev/net/tun \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -p 5270:5270 \
  --net=host \
  -v linuxwebtool-data:/app/data \
  -v /lwt:/lwt:rshared \
  -v /var/docker:/var/docker:rshared \
  -v /usr/local/bin:/usr/local/bin:ro \
  ghcr.io/csvkse/lwt:latest

# 4. 确认容器启动与健康检查
docker ps --filter name=linuxwebtool
docker logs --tail 30 linuxwebtool
```

### 阶段四：容器依赖与镜像源加速配置
在特权容器（Alpine Linux）环境下安装示例插件所需的全部多语言运行环境：

> [!WARNING]
> 在国内云服务器或部分 IPv6 网络环境中，Alpine 默认 Fastly CDN (`dl-cdn.alpinelinux.org`) 下载大包时极易发生 TCP 假死或降速。必须先切换为国内高速镜像源（如中科大源 USTC 或阿里源 Aliyun）再执行安装。

```bash
# 1. 替换为中科大加速源
docker exec linuxwebtool sed -i 's/dl-cdn.alpinelinux.org/mirrors.ustc.edu.cn/g' /etc/apk/repositories
docker exec linuxwebtool apk update

# 2. 全量安装插件依赖包
docker exec linuxwebtool apk add --no-cache \
  caddy \
  nodejs \
  python3 \
  py3-pip \
  dotnet8-sdk \
  powershell

# 3. 验证环境版本
docker exec linuxwebtool sh -c 'node -v && python3 --version && dotnet --version && pwsh -v && caddy version'
```

### 阶段五：内置浏览器自动化端到端验收
使用 Headless Chrome DevTools 进行高保真黑盒验证：

1. **登录会话建立**：
   - 导航至 `http://10.126.1.70:5270/app/#/login`。
   - 注入凭据或调用 `/api/auth/login` 取得 Token 并存入 `localStorage.lwt_token`。
2. **依赖引导安装功能验收**：
   - 导航至 `/app/#/plugins`。
   - 点击插件卡片上的 `🛠️ 安装引导` 或工具栏按钮。
   - 验证弹窗渲染：检查 Target Scope 单选框（当前容器 vs 宿主主机）、Alpine / Debian / RHEL / Windows 快捷填入按钮、在线编辑框与“在终端中执行”跳转。
3. **插件生命周期与反向代理验收**：
   - **4 个轻量脚本插件**：调用 `/api/Plugins/Action` 触发 `status`、`records`、`failed`、`ban_ip`、`validate`、`reload`、`info` 等动作，验证退出码为 0 且返回结构化 JSON。
   - **6 个常驻微服务插件**：调用 `POST /api/Plugins/{id}/Start` 启动各语言微服务，验证分配端口、PID 及自动守护能力；发起 HTTP 请求至各微服务网关路径（如 `/plugins/demo-js-webhook/`、`/plugins/demo-py-api/`、`/plugins/demo-dotnet-service/` 等），验证反向代理响应 HTTP 200。
4. **视觉留存与归档**：
   - 调用 `take_screenshot` 保存视口与全屏截图至制品目录作为验收留证。
