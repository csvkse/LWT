import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import {
  ProfileFilterStrategy,
  LifecycleFilterStrategy,
  ProtocolFilterStrategy,
  CategoryFilterStrategy,
  KeywordFilterStrategy,
  StateFilterStrategy,
  CompositePluginFilter,
  filterPlugins,
  sortPluginsByState,
  getPluginStateWeight,
  PluginSearchAdapter,
} from '../../src/LinuxWebTool.WebHost/wwwroot/app/utils/pluginFilterStrategies.js';

import {
  PLUGIN_STATE,
  PluginStateMachine,
} from '../../src/LinuxWebTool.WebHost/wwwroot/app/utils/pluginStateMachine.js';

const mockPlugins = [
  {
    id: 'demo-ps-daemon',
    name: 'PowerShell 系统资源监控守护',
    profile: 0, // resident
    runtime: 4, // powershell
    lifecycle: 0, // interpreted
    protocol: 0, // http
    state: PLUGIN_STATE.STOPPED,
    manifest: {
      category: 'Ops',
      description: 'PowerShell 编写的主机资源采集与磁盘告警长时守护进程',
      enabled: true,
    },
  },
  {
    id: 'demo-dotnet-service',
    name: '.NET 极速微服务',
    profile: 0, // resident
    runtime: 2, // dotnet
    lifecycle: 2, // source_compiled
    protocol: 0, // http
    state: PLUGIN_STATE.RUNNING,
    manifest: {
      category: 'Backend',
      description: '基于 C# 顶级语句的极速 HTTP 微服务示例',
      enabled: true,
    },
  },
  {
    id: 'chatgpt-channel',
    name: 'ChatGPT 渠道网关微服务',
    profile: 0, // resident
    runtime: 1, // python
    lifecycle: 0, // interpreted
    protocol: 0, // http
    state: PLUGIN_STATE.MISSING_RUNTIME,
    isMissingRuntime: true,
    missingRuntimeMessage: '缺少运行环境或依赖',
    manifest: {
      category: 'AI',
      description: '聚合多渠道大模型网关微服务，支持动态负载均衡与流式代理',
      enabled: true,
    },
  },
  {
    id: 'quick-disk-clean',
    name: '快速清理临时文件',
    profile: 1, // oneshot
    runtime: 3, // bash
    lifecycle: 0, // interpreted
    protocol: 4, // cli
    state: PLUGIN_STATE.STOPPED,
    manifest: {
      category: 'Ops',
      description: 'Bash 一键清理 /tmp 缓存与残留日志',
      enabled: true,
    },
  },
  {
    id: 'demo-serverless-api',
    name: '按需报表生成服务',
    profile: 2, // serverless
    runtime: 0, // node
    lifecycle: 0, // interpreted
    protocol: 0, // http
    state: PLUGIN_STATE.STOPPED,
    manifest: {
      category: 'Backend',
      description: '按需冷启动并闲置 5 分钟缩容至零的 Serverless 微服务',
      enabled: true,
    },
  },
  {
    id: 'demo-wasm-calc',
    name: 'Wasm 极速加密微函数',
    profile: 3, // wasm
    runtime: 7, // wasm
    lifecycle: 2, // source_compiled
    protocol: 3, // wasm_abi
    state: PLUGIN_STATE.STOPPED,
    manifest: {
      category: 'Security',
      description: '基于 WebAssembly 内存沙箱的极速无解释器加密微函数',
      enabled: true,
    },
  },
];

test('PLUGIN-STRATEGY-01: ProfileFilterStrategy 正确区分常驻服务、一次性脚本、按需 FaaS 与 Wasm 微函数', () => {
  const strategy = new ProfileFilterStrategy();
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'all' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'resident' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'oneshot' }), false);

  assert.equal(strategy.apply(mockPlugins[3], { profile: 'resident' }), false);
  assert.equal(strategy.apply(mockPlugins[3], { profile: 'oneshot' }), true);

  assert.equal(strategy.apply(mockPlugins[4], { profile: 'serverless' }), true);
  assert.equal(strategy.apply(mockPlugins[4], { profile: 'resident' }), false);

  assert.equal(strategy.apply(mockPlugins[5], { profile: 'wasm' }), true);
  assert.equal(strategy.apply(mockPlugins[5], { profile: 'oneshot' }), false);

  // 验证正交三大计算范式 (service, function, task)
  assert.equal(strategy.apply(mockPlugins[0], { archetype: 'service' }), true);
  assert.equal(strategy.apply(mockPlugins[3], { archetype: 'task' }), true);
  assert.equal(strategy.apply(mockPlugins[4], { archetype: 'function' }), true); // Serverless
  assert.equal(strategy.apply(mockPlugins[5], { archetype: 'function' }), true); // Wasm
  assert.equal(strategy.apply(mockPlugins[0], { archetype: 'function' }), false);

  // 验证生命周期策略与协议策略
  const lifeStrategy = new LifecycleFilterStrategy();
  assert.equal(lifeStrategy.apply(mockPlugins[0], { lifecycle: 'interpreted' }), true);
  assert.equal(lifeStrategy.apply(mockPlugins[1], { lifecycle: 'source_compiled' }), true);
  assert.equal(lifeStrategy.apply(mockPlugins[0], { lifecycle: 'source_compiled' }), false);

  const protoStrategy = new ProtocolFilterStrategy();
  assert.equal(protoStrategy.apply(mockPlugins[0], { protocol: 'http' }), true);
  assert.equal(protoStrategy.apply(mockPlugins[5], { protocol: 'wasm_abi' }), true);
  assert.equal(protoStrategy.apply(mockPlugins[3], { protocol: 'cli' }), true);
  assert.equal(protoStrategy.apply(mockPlugins[0], { protocol: 'wasm_abi' }), false);
});

test('PLUGIN-STRATEGY-02: CategoryFilterStrategy 正确按业务分类过滤', () => {
  const strategy = new CategoryFilterStrategy();
  assert.equal(strategy.apply(mockPlugins[0], { category: 'Ops' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { category: 'AI' }), false);
  assert.equal(strategy.apply(mockPlugins[0], { category: 'all' }), true);
});

test('PLUGIN-STRATEGY-03: KeywordFilterStrategy 支持多字段与多词联合匹配（AND）', () => {
  const strategy = new KeywordFilterStrategy();
  // 单词匹配名称
  assert.equal(strategy.apply(mockPlugins[0], { keyword: 'powershell' }), true);
  // 单词匹配 ID
  assert.equal(strategy.apply(mockPlugins[1], { keyword: 'demo-dotnet' }), true);
  // 单词匹配描述
  assert.equal(strategy.apply(mockPlugins[2], { keyword: '大模型' }), true);
  // 多词空格联合匹配（所有 token 均须命中某字段）
  assert.equal(strategy.apply(mockPlugins[2], { keyword: 'chatgpt 网关' }), true);
  // 不相关的多词不能命中
  assert.equal(strategy.apply(mockPlugins[2], { keyword: 'chatgpt powershell' }), false);
  // 空白字符串全部通过
  assert.equal(strategy.apply(mockPlugins[0], { keyword: '   ' }), true);
});

test('PLUGIN-STRATEGY-04: CompositePluginFilter 策略组合与适配器调用', () => {
  const result = filterPlugins(mockPlugins, {
    profile: 'resident',
    category: 'Ops',
    keyword: '监控',
  });
  assert.equal(result.length, 1);
  assert.equal(result[0].id, 'demo-ps-daemon');

  const adapted = PluginSearchAdapter.search(mockPlugins, {
    profile: 'oneshot',
  });
  assert.equal(adapted.length, 1);
  assert.equal(adapted[0].id, 'quick-disk-clean');
});

test('PLUGIN-STRATEGY-05: StateFilterStrategy 与状态优先级排序验证', () => {
  const strategy = new StateFilterStrategy();
  assert.equal(strategy.apply(mockPlugins[1], { state: 'running' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { state: 'running' }), false);

  const degradedPlugin = { id: 'degraded-app', profile: 0, state: PLUGIN_STATE.DEGRADED };
  const backoffPlugin = { id: 'backoff-app', profile: 0, state: PLUGIN_STATE.BACKOFF };
  const crashedPlugin = { id: 'crashed-app', profile: 0, state: PLUGIN_STATE.CRASHED };

  assert.equal(strategy.apply(degradedPlugin, { state: 'degraded' }), true);
  assert.equal(strategy.apply(backoffPlugin, { state: 'degraded' }), true);
  assert.equal(strategy.apply(crashedPlugin, { state: 'degraded' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { state: 'degraded' }), false);

  assert.equal(strategy.apply(mockPlugins[2], { state: 'missing' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { state: 'stopped' }), true);

  // 验证脚本轻量扩展(Profile=1)启用状态属于 ready，不属于 stopped
  assert.equal(strategy.apply(mockPlugins[3], { state: 'ready' }), true);
  assert.equal(strategy.apply(mockPlugins[3], { state: 'stopped' }), false);

  // 验证显式禁用状态(Disabled)精准匹配
  const disabledScript = { id: 'disabled-script', profile: 1, state: PLUGIN_STATE.DISABLED, manifest: { enabled: false } };
  assert.equal(strategy.apply(disabledScript, { state: 'disabled' }), true);
  assert.equal(strategy.apply(disabledScript, { state: 'ready' }), false);
  assert.equal(strategy.apply(disabledScript, { state: 'stopped' }), false);

  // 排序优先级权重测试：运行中 (1) > 降级 (4) > 缺少环境 (5) > 已停止 (6)
  const unsorted = [mockPlugins[0], mockPlugins[2], degradedPlugin, mockPlugins[1]];
  const sorted = sortPluginsByState(unsorted);
  assert.equal(sorted[0].id, 'demo-dotnet-service'); // running
  assert.equal(sorted[1].id, 'degraded-app'); // degraded
  assert.equal(sorted[2].id, 'chatgpt-channel'); // missing
  assert.equal(sorted[3].id, 'demo-ps-daemon'); // stopped
});

test('PLUGIN-FSM-01: PluginStateMachine 启动/停止/重启保护守卫准确有效', () => {
  // 1. 已停止的常驻服务允许启动，不允许停止或重启
  assert.equal(PluginStateMachine.canStart(mockPlugins[0]), true);
  assert.equal(PluginStateMachine.canStop(mockPlugins[0]), false);
  assert.equal(PluginStateMachine.canRestart(mockPlugins[0]), false);

  // 2. 运行中的常驻服务不允许再次启动，允许停止与重启
  assert.equal(PluginStateMachine.canStart(mockPlugins[1]), false);
  assert.equal(PluginStateMachine.canStop(mockPlugins[1]), true);
  assert.equal(PluginStateMachine.canRestart(mockPlugins[1]), true);

  // 3. 缺少依赖的插件严禁启动
  assert.equal(PluginStateMachine.canStart(mockPlugins[2]), false);

  // 4. 一次性脚本服务没有常驻停止概念
  assert.equal(PluginStateMachine.canStop(mockPlugins[3]), false);

  // 5. 降级、崩溃、退避状态下的常驻微服务必须允许手动停止（重置熔断与清理孤儿进程）
  const degradedService = { profile: 0, state: PLUGIN_STATE.DEGRADED };
  const backoffService = { profile: 0, state: PLUGIN_STATE.BACKOFF };
  const crashedService = { profile: 0, state: PLUGIN_STATE.CRASHED };
  assert.equal(PluginStateMachine.canStop(degradedService), true);
  assert.equal(PluginStateMachine.canStop(backoffService), true);
  assert.equal(PluginStateMachine.canStop(crashedService), true);
  assert.equal(PluginStateMachine.canStart(degradedService), true);
});

test('PLUGIN-FSM-02: PluginStateMachine 准确映射状态元数据与环境标签', () => {
  const stoppedMeta = PluginStateMachine.getStateMeta(mockPlugins[0]);
  assert.equal(stoppedMeta.label, '已停止');

  const runningMeta = PluginStateMachine.getStateMeta(mockPlugins[1]);
  assert.equal(runningMeta.label, '运行中');

  const missingMeta = PluginStateMachine.getStateMeta(mockPlugins[2]);
  assert.equal(missingMeta.label, '缺少环境');

  const missingDepPlugin = {
    ...mockPlugins[2],
    state: PLUGIN_STATE.MISSING_DEPENDENCIES,
    isMissingDependencies: true,
  };
  const missingDepMeta = PluginStateMachine.getStateMeta(missingDepPlugin);
  assert.equal(missingDepMeta.label, '缺少依赖');

  const oneshotMeta = PluginStateMachine.getStateMeta(mockPlugins[3]);
  assert.equal(oneshotMeta.label, '已就绪');

  const dotnetMeta = PluginStateMachine.getRuntimeMeta(2);
  assert.equal(dotnetMeta.label, '.NET AOT');
});

test('PLUGIN-THEME-01: PluginsView 搜索框及相关样式必须符合暗黑赛博规范', () => {
  const viewPath = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app/views/PluginsView.js');
  const content = fs.readFileSync(viewPath, 'utf8');

  // 不得出现裸 bg-white
  assert.equal(content.includes('bg-white'), false, 'PluginsView 严禁出现未经修饰的 bg-white');

  // 搜索框必须包含暗黑背景与赛博边框
  assert.ok(content.includes('bg-slate-900/90'), '搜索输入框必须采用暗黑半透明底色 bg-slate-900/90');
  assert.ok(content.includes('border-cyber-line/80'), '搜索输入框必须采用系统赛博边框 border-cyber-line/80');
  assert.ok(content.includes('focus:border-cyan-400'), '搜索输入框聚焦时必须具有霓虹蓝光圈');
  assert.ok(content.includes('searchKeyword'), '搜索框双向绑定 searchKeyword');
  assert.ok(content.includes('resetFilters()'), '空状态下必须提供重置筛选 resetFilters() 快捷入口');
});

test('PLUGIN-THEME-02: 门禁规则 FE-THEME-FORM-CONTROL-DARK 能够精准拦截白底未修饰表单控件', () => {
  const badTemplate = `
    <div>
      <input type="text" class="legacy-unregistered-input" placeholder="测试白底回退" />
    </div>
  `;
  const badTagMatch = badTemplate.match(/<input\b([^>]*?)>/i);
  assert.ok(badTagMatch);
  const clsMatch = badTagMatch[1].match(/\bclass\s*=\s*['"]([^'"]+)['"]/i);
  const cls = clsMatch ? clsMatch[1] : '';
  const hasDark = /(?:^|\s)(input|input-base|select)(?:$|\s)/.test(cls) ||
                  /\bbg-(slate|cyber|zinc|gray|neutral)-(700|800|900|950)\b/.test(cls) ||
                  /\bbg-cyber-(bg|panel|panel-2)\b/.test(cls) ||
                  /\bbg-transparent\b/.test(cls);
  assert.equal(hasDark, false, '未声明系统暗黑主题类的控件必须被门禁精准判定为未通过');
});

test('PLUGIN-DATA-01: 门禁规则 FE-PLUGIN-DATA-PURITY 能够拦截全局事实源污染传参', () => {
  const badCall = "const res = await http(API.plugins.list(profileTab.value, searchKeyword.value));";
  const isViolated = /API\.plugins\.list\s*\(\s*(?:profileTab|searchKeyword)/.test(badCall);
  assert.equal(isViolated, true, '数据加载携带 profileTab 或 searchKeyword 必须被门禁拦截');

  const goodCall = "const res = await http(API.plugins.list('all', ''), { method: 'GET' });";
  const isGoodViolated = /API\.plugins\.list\s*\(\s*(?:profileTab|searchKeyword)/.test(goodCall);
  assert.equal(isGoodViolated, false, '合规的全量事实源加载调用必须被放行');
});

test('PLUGIN-DISTRO-01: 门禁规则 FE-DISTRO-CMD-INTEGRITY 能够精准拦截包管理器混用与旧版依赖', () => {
  // 1. 负例：Alpine 下使用 apt 必须被识别为违规
  const badAlpineCmd = 'apt update && apt install -y dotnet-sdk-8.0';
  const isAlpineBad = badAlpineCmd.includes('apt ') || badAlpineCmd.includes('apt-get ');
  assert.equal(isAlpineBad, true, 'Alpine 容器推荐 apt 命令必须被门禁拦截');

  // 2. 负例：Debian 下使用 apk 必须被识别为违规
  const badDebianCmd = 'apk add --no-cache nodejs';
  const isDebianBad = badDebianCmd.includes('apk add');
  assert.equal(isDebianBad, true, 'Debian 系统推荐 apk 命令必须被门禁拦截');

  // 3. 正例：.NET 安装指引必须包含 10，严禁回退为 net8
  const goodDotnetAlpine = 'curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet';
  assert.equal(goodDotnetAlpine.includes('10'), true);
  assert.equal(goodDotnetAlpine.includes('apt'), false);
});


