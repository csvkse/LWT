// tests/frontend/forwarder-crud-lifecycle.test.mjs
// 测试 SOCKS5 中继网关前端 CRUD 闭环与拓扑解绑防回归门禁

import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';

describe('SOCKS5 Forwarder CRUD Lifecycle & Topology Governance Tests', () => {
  const uiDir = path.resolve('plugins/socks5-forwarder/ui');

  it('CRUD-01: drawer.js 必须具备完整的 6 大实体抽屉接口定义', () => {
    const drawerCode = fs.readFileSync(path.join(uiDir, 'drawer.js'), 'utf-8');
    assert.match(drawerCode, /openUpstream\s*\(/, '必须导出 openUpstream 抽屉方法');
    assert.match(drawerCode, /openPipeline\s*\(/, '必须导出 openPipeline 抽屉方法');
    assert.match(drawerCode, /openInbound\s*\(/, '必须导出 openInbound 抽屉方法');
    assert.match(drawerCode, /openChain\s*\(/, '必须导出 openChain 抽屉方法');
    assert.match(drawerCode, /openImport\s*\(/, '必须导出 openImport 抽屉方法');
    assert.match(drawerCode, /openTun\s*\(/, '必须导出 openTun 抽屉方法');
  });

  it('CRUD-02: openInbound 下拉列表中必须包含空线路选项以支持解绑', () => {
    const drawerCode = fs.readFileSync(path.join(uiDir, 'drawer.js'), 'utf-8');
    assert.match(drawerCode, /<option value="">-- \(暂不挂载，设为待命接入网关\) --<\/option>/, 'Inbound 抽屉必须允许解绑至待命池');
  });

  it('CRUD-03: topology.js 必须实现解绑微交互与待命接入网关池', () => {
    const topoCode = fs.readFileSync(path.join(uiDir, 'topology.js'), 'utf-8');
    assert.match(topoCode, /btn-unbind-inbound/, '拓扑管道卡片必须包含 btn-unbind-inbound 解绑微按钮');
    assert.match(topoCode, /btn-del-inbound/, '拓扑管道卡片必须包含 btn-del-inbound 彻底销毁按钮');
    assert.match(topoCode, /btn-del-pipe/, '拓扑管道卡片必须包含 btn-del-pipe 线路删除按钮');
    assert.match(topoCode, /待命接入网关池/, '拓扑画布必须提供待命接入网关池消除孤儿黑洞');
    assert.match(topoCode, /sel-bind-standby/, '待命网关池必须支持快速挂载到线路');
  });

  it('CRUD-04: rule_builder.js 必须支持规则条目优先级上下移动', () => {
    const ruleCode = fs.readFileSync(path.join(uiDir, 'rule_builder.js'), 'utf-8');
    assert.match(ruleCode, /btn-move-up/, '规则条目必须包含上移按钮');
    assert.match(ruleCode, /btn-move-down/, '规则条目必须包含下移按钮');
  });

  it('CRUD-05: nodes.js 必须支持节点卡片快捷删除与使用中依赖保护', () => {
    const nodeCode = fs.readFileSync(path.join(uiDir, 'nodes.js'), 'utf-8');
    assert.match(nodeCode, /btn-del-node/, '节点卡片必须包含直接删除按钮');
    assert.match(nodeCode, /defaultOutbound === node\.id/, '删除节点前必须检测是否被流水线设为出口');
  });

  it('CRUD-06: 顶部导航条必须包含全部核心快捷动作入口', () => {
    const htmlCode = fs.readFileSync(path.join(uiDir, 'index.html'), 'utf-8');
    assert.match(htmlCode, /btnQuickAddInbound/, '必须包含接入创建按钮');
    assert.match(htmlCode, /btnQuickAddPipe/, '必须包含线路创建按钮');
    assert.match(htmlCode, /btnQuickAddChain/, '必须包含链式代理创建按钮');
    assert.match(htmlCode, /btnQuickImport/, '必须包含链接导入按钮');
  });
});
