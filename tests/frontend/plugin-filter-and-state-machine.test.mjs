import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import {
  ProfileFilterStrategy,
  CategoryFilterStrategy,
  KeywordFilterStrategy,
  CompositePluginFilter,
  filterPlugins,
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
    state: PLUGIN_STATE.STOPPED,
    manifest: {
      category: 'Ops',
      description: 'Bash 一键清理 /tmp 缓存与残留日志',
      enabled: true,
    },
  },
];

test('PLUGIN-STRATEGY-01: ProfileFilterStrategy 正确区分常驻服务与一次性脚本', () => {
  const strategy = new ProfileFilterStrategy();
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'all' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'resident' }), true);
  assert.equal(strategy.apply(mockPlugins[0], { profile: 'oneshot' }), false);

  assert.equal(strategy.apply(mockPlugins[3], { profile: 'resident' }), false);
  assert.equal(strategy.apply(mockPlugins[3], { profile: 'oneshot' }), true);
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
});

test('PLUGIN-FSM-02: PluginStateMachine 准确映射状态元数据与环境标签', () => {
  const stoppedMeta = PluginStateMachine.getStateMeta(mockPlugins[0]);
  assert.equal(stoppedMeta.label, '已停止');

  const runningMeta = PluginStateMachine.getStateMeta(mockPlugins[1]);
  assert.equal(runningMeta.label, '运行中');

  const missingMeta = PluginStateMachine.getStateMeta(mockPlugins[2]);
  assert.equal(missingMeta.label, '缺少依赖');

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
