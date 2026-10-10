# GitHub 冒烟性能指标实施方案

## 目标和边界

给已有 API 黑盒冒烟增加启动、接口延迟、请求错误和资源占用证据，在 GitHub artifact 和任务摘要中可查看。先建立历史基线，功能契约/超时继续阻止发布；不以托管 runner 的单次延迟波动判定性能退化。

本次是低负载串行性能冒烟，不是压力测试；真实 TUN/SMB 故障期间的登录延迟还需独立 fixture，目前不宣称已覆盖。

## 指标与定义

| 指标 | 采集方式 | 解释 |
| --- | --- | --- |
| 启动就绪耗时 | 从启动程序/容器命令前到 `/health` 首次返回 200 | 含启动命令、Node 启动与就绪轮询误差；同时记录探测次数 |
| 登录与接口 P50/P95/P99 | login、authCheck、commands、easyTierNodes 各预热两次、串行二十次 | 完整请求、消费响应体及 JSON 解析耗时；nearest-rank 分位数；P99 小样本仅描述性参考 |
| 请求错误率 | 性能采样失败数 / 已执行采样数 | 状态契约、解析或超时错误均失败；预期 401 的功能测试不计为性能失败 |
| 观测请求速率 | 采样数 / 该接口采样窗口秒数 | 并发为 1，不代表最大 QPS、容量或饱和吞吐量 |
| 桌面资源 | 本次启动进程前后快照 | CPU 累计秒、RSS、私有内存、线程数；整个冒烟窗口 CPU 平均单核等效百分比，可超过 100% |
| 容器资源 | `docker stats --no-stream` 前后快照 | Docker CPU%、内存使用/比例和 PIDs（含线程的任务数），保留 CLI 单位；不能与桌面线程数直接等同 |

资源采样是两次快照，不是峰值/连续监控；不可用字段明确标注 unavailable。API 延迟是客户端视角，不能单独拆分服务端处理和网络耗时。

## 实施步骤

1. 新增 `scripts/smoke-performance.mjs`，导出统计、采样、基线比较和 JSON/摘要保存函数；测试分位数、错误率、空样本和基线边界。
2. `smoke-http.mjs` 在功能契约验证成功后运行性能采样；失败时保存部分已完成样本并终止冒烟。记录 schemaVersion、时间、commit、runner OS/架构、CPU 型号/核心数、主机内存、Node 版本、目标/运行模式和方法。
3. 启动脚本传入启动时刻和报告位置，保存资源快照并恢复环境变量。桌面输出到本次唯一目录，容器输出到当前 artifact 目录。
4. CI 上传 `performance.json` 和 `resources.json`；GitHub Step Summary 显示启动和接口分位数/失败数。桌面上传白名单只增加这两个报告，继续排除包含凭据的数据目录。
5. 通过 `SMOKE_PERF_BASELINE` 指定先前 JSON，输出 P95 变化率及目标、运行模式、OS、架构、CPU、Node 主版本和采样方法兼容性；仅供观察，不阻止发布。基线缺失/无效标注 unavailable，仍保存当前证据。不同环境不得直接做退化结论。

报告不保存账号、密码、token、请求体、服务地址、私人文件路径或任意异常全文。动态临时 ID 不用于接口标识，使用四个固定名称。原始耗时仅保存数值与成功标记。

## 基线和后续门禁

先收集同类型 runner、相同镜像驱动档/平台、相同方法下至少十次成功运行，观察中位数和波动区间。本次不自动下载历史 artifact，也不实现跨运行趋势图；每次保存的 JSON 是后续分析依据。

后续需要自动性能门禁时，应在稳定 runner 上定义绝对上限、相对变化和连续超限规则。没有基线不设置猜测阈值，不拿二十个样本的 P99 当作 SLA。CPU/内存峰值、并发压测和故障下登录延迟属于单独扩展范围。

## 本地执行

```powershell
node --test tests/smoke/http-contract.test.mjs tests/smoke/performance.test.mjs
./scripts/smoke-published.ps1 -PublishDirectory ./artifacts/ci-local -Port 15275
# 容器先构建镜像，再执行：
./scripts/smoke-aot.ps1 -ImageTag linuxwebtool:aot-verify
# 可选比较先前指标：
$env:SMOKE_PERF_BASELINE = '上一份 performance.json 的路径'
```

只能针对隔离临时服务执行；功能冒烟包含写入。无需部署或修改生产服务。

## 实施与验收

- [x] 分位数、失败率、原始样本、启动就绪、基线比较与任务摘要。
- [x] 桌面进程/容器资源快照与 CI artifact 接入。
- [x] 测试先行：新增测试因实现文件缺失失败，最小实现后通过。
- [x] 本地 Windows x64 managed 发布程序实际运行，生成并验证 JSON、资源快照、任务摘要；四个接口各二十次、无失败；可选基线比较兼容性全部为 true。
- 本轮示例：启动 1992 ms；登录 P50/P95/P99 为 16.9/28.5/30.1 ms，Auth Check 为 12.8/16.5/22.3 ms，Commands 为 15.1/16.5/17.7 ms，EasyTier Nodes 为 11.7/16.2/18.4 ms。
- 结束快照 RSS 105.6 MiB，线程 39，进程累计 CPU 3.125 秒。这是含启动和功能冒烟的窗口，不是单纯的性能采样窗口或峰值。
- 报告保存在 `artifacts/desktop-smoke/<本次唯一目录>/performance.json` 与 `resources.json`；本轮目录为 `40be5f15176047a4be4148e28e9cc2d5`，这些临时证据被 Git 忽略，CI 会上传同格式 artifact。
- Linux Docker 资源采样和 GitHub runner 未实际执行；不能以本地 Windows 结果替代。尚未推送代码、触发 GitHub Actions 或修改远程设置。
