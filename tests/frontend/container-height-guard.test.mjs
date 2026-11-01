import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

function checkTemplateContainerLimits(tpl, content = '') {
  const violations = [];
  const forMatches = tpl.matchAll(/<([a-z0-9-]+)\b([^>]*?v-for\s*=\s*["']([^"']+)["'][^>]*)>/gi);

  for (const match of forMatches) {
    const tagName = match[1].toLowerCase();
    const forExpr = match[3];

    // 1. 豁免元素与短静态列表 (不属于长列表容器)
    const isExemptTag = ['option', 'button', 'span', 'label', 'router-link', 'template', 'a', 'kbd'].includes(tagName);
    const isExemptVar = /\b(tabs|groups|options|breadcrumbs|presets|columns|commonPresets|excludeChips|categories|distroOptions|TABS|RANGES|PROCESS_SORTS|QUICK_COMMANDS|EXEC_PRESETS|STATUS_OPTIONS|SOURCE_OPTIONS|PROTOCOL_OPTIONS|NAV_GROUPS|hardware|disks|networks|stages|steps|argumentBindings|artifactRules|fields|toastState\.items|moduleCatalog)\b/i.test(forExpr);
    if (isExemptTag || isExemptVar) continue;

    // 2. 检查数据源是否已接入逻辑数据有界控制 (分页切片 pagedUnits, pagination.pagedItems, slice, 或视图自带分页控制器)
    const isPagedData = /\b(paged\w*|slice\s*\(|pagination\.)/i.test(forExpr);
    const hasPaginationControl = /\b(PaginationBar|pagination-bar)\b/i.test(tpl) ||
      (/\b(page|pageSize)\b/i.test(content) && /\b(total|totalPages)\b/i.test(content));
    if (isPagedData || hasPaginationControl) continue;

    // 3. 向上回溯分析宿主容器是否有物理高度约束
    const matchIdx = match.index;
    const surroundingSnippet = tpl.slice(Math.max(0, matchIdx - 1800), matchIdx + match[0].length);
    const hasMaxHeight = /\b(?:max-h-|h-\[|h-full|h-screen)/.test(surroundingSnippet);
    const hasFlexScroll = /\bflex-1\b/.test(surroundingSnippet) && /\boverflow-(?:y-)?(?:auto|scroll)\b/.test(surroundingSnippet);
    const hasBoundedClass = /\b(output-block|bounded-list|list-viewport|modal-list-viewport|card-list-viewport)\b/.test(surroundingSnippet);

    if (!hasMaxHeight && !hasFlexScroll && !hasBoundedClass) {
      violations.push({ forExpr, tagName });
    }
  }
  return violations;
}

test('CONTAINER-GUARD-01: 具有 list-viewport 或 max-h 约束的容器必须顺利通过门禁', () => {
  const boundedTpl = `
    <div class="panel overflow-x-auto list-viewport">
      <table class="data-table">
        <tbody>
          <tr v-for="c in containers" :key="c.id"><td>{{ c.name }}</td></tr>
        </tbody>
      </table>
    </div>
  `;
  const violations = checkTemplateContainerLimits(boundedTpl);
  assert.equal(violations.length, 0, '受控的 list-viewport 容器应通过门禁');
});

test('CONTAINER-GUARD-02: 接入分页切片 (如 pagedUnits) 的长列表天然具备数据有界性，通过门禁', () => {
  const pagedTpl = `
    <div class="panel">
      <table class="data-table">
        <tbody>
          <tr v-for="u in pagedUnits" :key="u.id"><td>{{ u.name }}</td></tr>
        </tbody>
      </table>
    </div>
  `;
  const violations = checkTemplateContainerLimits(pagedTpl);
  assert.equal(violations.length, 0, '分页数据切片应通过门禁');
});

test('CONTAINER-GUARD-03: 短静态 UI 元素 (option/button/tabs) 必须被正确识别与豁免', () => {
  const staticTpl = `
    <div>
      <select><option v-for="opt in pageSizeOptions" :value="opt">{{ opt }}</option></select>
      <button v-for="t in tabs" :key="t.key">{{ t.title }}</button>
      <span v-for="crumb in breadcrumbs" :key="crumb.path">{{ crumb.name }}</span>
    </div>
  `;
  const violations = checkTemplateContainerLimits(staticTpl);
  assert.equal(violations.length, 0, '静态小规模 UI 列表不应被误判');
});

test('CONTAINER-GUARD-04: 既无高度约束又无分页的无界长列表，必须被门禁准确拦截', () => {
  const unboundedTpl = `
    <div class="panel overflow-x-auto">
      <table class="data-table">
        <tbody>
          <tr v-for="item in rawUnboundedRemoteRecords" :key="item.id"><td>{{ item.data }}</td></tr>
        </tbody>
      </table>
    </div>
  `;
  const violations = checkTemplateContainerLimits(unboundedTpl);
  assert.equal(violations.length, 1, '无界长列表必须被拦截');
  assert.equal(violations[0].forExpr, 'item in rawUnboundedRemoteRecords');
});

test('CONTAINER-GUARD-05: 生产全站代码健康度校验：所有前端模板满足高度或分页受控约束', () => {
  const appDir = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app');
  function walk(dir, list = []) {
    for (const f of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, f.name);
      if (f.isDirectory()) {
        if (f.name === 'vendor' || f.name === 'node_modules') continue;
        walk(full, list);
      } else if (f.name.endsWith('.js')) list.push(full);
    }
    return list;
  }
  const files = walk(appDir);
  const prodViolations = [];
  for (const file of files) {
    const content = fs.readFileSync(file, 'utf8');
    const tplMatch = content.match(/template:\s*`([\s\S]*?)`/);
    if (!tplMatch) continue;
    const violations = checkTemplateContainerLimits(tplMatch[1], content);
    for (const v of violations) {
      prodViolations.push({ file: path.relative(appDir, file), ...v });
    }
  }

  assert.equal(
    prodViolations.length,
    0,
    `生产环境应无违规列表容器，但检测到: ${JSON.stringify(prodViolations, null, 2)}`
  );
});
