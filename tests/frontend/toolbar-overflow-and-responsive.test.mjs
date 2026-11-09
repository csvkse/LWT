import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

const pluginsViewPath = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app/views/PluginsView.js');
const pluginsViewSource = fs.readFileSync(pluginsViewPath, 'utf8');

test('TOOLBAR-RESPONSIVE-01: PluginsView 核心控制区必须采用双层解耦架构，严禁使用单行死锁拥挤结构', () => {
  // 断言不存在旧版的死锁类名组合：flex xl:flex-row 与 overflow-x-auto 强行塞入全部下拉与按钮
  const hasLegacyDeadlock = /class\s*=\s*["'][^"']*?xl:flex-row[^"']*?["'][\s\S]*?v-model="searchKeyword"[\s\S]*?showCreateModal/i.test(pluginsViewSource);
  assert.equal(hasLegacyDeadlock, false, 'PluginsView 不得在单行 flex-row 容器内同时混杂 Tabs、4个下拉框、搜索框与新建按钮');

  // 断言必须包含第 1 层与第 2 层分层结构注释与两段式容器
  assert.match(pluginsViewSource, /双层语义解耦控制台/i, '必须采用双层语义解耦控制台');
  assert.match(pluginsViewSource, /第 1 层：三大正交工作负载/i, '必须明确具备第 1 层工作负载导航');
  assert.match(pluginsViewSource, /第 2 层：全文检索与多维切面过滤/i, '必须明确具备第 2 层检索与过滤工具栏');
});

test('TOOLBAR-RESPONSIVE-02: 新建插件与导入等核心 CTA 按钮必须位于无 overflow-x 截断的稳定容器中', () => {
  const buttonSnippetMatch = pluginsViewSource.match(/<button\b[^>]*?@click="showCreateModal\s*=\s*true"[\s\S]*?<\/button>/i);
  assert.ok(buttonSnippetMatch, '必须存在新建插件按钮');

  const createModalIndex = pluginsViewSource.indexOf('showCreateModal = true');
  const contextBefore = pluginsViewSource.slice(Math.max(0, createModalIndex - 600), createModalIndex);

  // 断言该按钮的上层直接包裹容器未声明 overflow-x-auto（防止水平截断）
  const directContainerMatch = contextBefore.match(/<div\b[^>]*?class\s*=\s*["']([^"']*)["'][^>]*>[^<]*$/i);
  if (directContainerMatch) {
    assert.doesNotMatch(
      directContainerMatch[1],
      /\boverflow-x-auto\b/,
      '新建插件按钮直接父级容器不得包含 overflow-x-auto，以防止核心 CTA 被滚动截断'
    );
  }
});

test('TOOLBAR-RESPONSIVE-03: 全文搜索框必须声明 flex-1 弹性拉伸容器，禁止硬编码狭窄固定宽', () => {
  const searchInputMatch = pluginsViewSource.match(/<input\b[^>]*?v-model="searchKeyword"[^>]*>/i);
  assert.ok(searchInputMatch, '必须包含 searchKeyword 搜索输入框');

  const keywordIndex = pluginsViewSource.indexOf('v-model="searchKeyword"');
  const searchContainerContext = pluginsViewSource.slice(
    Math.max(0, keywordIndex - 250),
    keywordIndex + 250
  );

  // 校验包含弹性自适应拉伸类 flex-1
  assert.match(searchContainerContext, /\bflex-1\b/, '搜索框容器必须具备 flex-1 弹性拉伸属性，以充分利用横向空间');
});

test('TOOLBAR-RESPONSIVE-04: 所有切面下拉框 (Select) 必须遵循暗黑主题规范，且具备标准尺寸阶梯', () => {
  const selectMatches = Array.from(
    pluginsViewSource.matchAll(/<select\b([^>]*?v-model="(?:lifecycleFilter|protocolFilter|stateFilter|categoryFilter)"[^>]*)>/gi)
  );
  assert.ok(selectMatches.length >= 3, '至少应检测到 3 个以上的策略切面下拉框');

  for (const match of selectMatches) {
    const attrs = match[1];
    assert.match(attrs, /\binput\b/, '下拉框必须具备 .input 类');
    assert.match(attrs, /\bbg-slate-900/, '下拉框必须具备暗黑色底色');
    assert.match(attrs, /\bselect-compact\b/, '下拉框必须具备 select-compact 紧凑类');
  }
});

test('TOOLBAR-RESPONSIVE-05: 必须提供一键重置全部过滤条件按钮，保障多维筛选后可快速复原', () => {
  assert.match(pluginsViewSource, /@click="resetFilters\(\)"/i, '必须绑定 resetFilters() 重置函数');
  assert.match(pluginsViewSource, /title="重置所有筛选条件"/i, '必须具备重置筛选提示');
});
