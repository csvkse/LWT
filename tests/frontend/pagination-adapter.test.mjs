import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';
import * as vue from '../../src/LinuxWebTool.WebHost/wwwroot/app/vendor/vue.esm-browser.prod.js';

const paginationUrl = new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/utils/pagination.js', import.meta.url);

async function loadPaginationModule() {
  const context = vm.createContext({});
  const mod = new vm.SourceTextModule(await readFile(paginationUrl, 'utf8'), { context });
  await mod.link((specifier) => {
    if (specifier === 'vue') {
      const synthetic = new vm.SyntheticModule(
        Object.keys(vue),
        function () {
          for (const [k, v] of Object.entries(vue)) {
            this.setExport(k, v);
          }
        },
        { context }
      );
      return synthetic;
    }
    throw new Error(`Unexpected import: ${specifier}`);
  });
  await mod.evaluate();
  return mod.namespace;
}

const { usePagination } = await loadPaginationModule();
const { ref, nextTick } = vue;

test('PAGINATION-01: 基础切片与区间计算正确', () => {
  const mockItems = ref(Array.from({ length: 45 }, (_, i) => ({ id: i + 1, name: `item-${i + 1}` })));
  const pager = usePagination(mockItems, { defaultPageSize: 20 });

  assert.equal(pager.totalItems.value, 45);
  assert.equal(pager.totalPages.value, 3);
  assert.equal(pager.currentPage.value, 1);
  assert.equal(pager.startIndex.value, 1);
  assert.equal(pager.endIndex.value, 20);
  assert.equal(pager.pagedItems.value.length, 20);
  assert.equal(pager.pagedItems.value[0].id, 1);
  assert.equal(pager.pagedItems.value[19].id, 20);
});

test('PAGINATION-02: 翻页控制与边界约束', () => {
  const mockItems = ref(Array.from({ length: 45 }, (_, i) => ({ id: i + 1 })));
  const pager = usePagination(mockItems, { defaultPageSize: 20 });

  pager.nextPage();
  assert.equal(pager.currentPage.value, 2);
  assert.equal(pager.startIndex.value, 21);
  assert.equal(pager.endIndex.value, 40);
  assert.equal(pager.pagedItems.value.length, 20);

  pager.nextPage();
  assert.equal(pager.currentPage.value, 3);
  assert.equal(pager.startIndex.value, 41);
  assert.equal(pager.endIndex.value, 45);
  assert.equal(pager.pagedItems.value.length, 5);

  // 超过末页不能再加
  pager.nextPage();
  assert.equal(pager.currentPage.value, 3);

  // 上一页与首部约束
  pager.prevPage();
  assert.equal(pager.currentPage.value, 2);
  pager.firstPage();
  assert.equal(pager.currentPage.value, 1);
  pager.prevPage();
  assert.equal(pager.currentPage.value, 1);

  // 末页跳转
  pager.lastPage();
  assert.equal(pager.currentPage.value, 3);

  // 任意页码设定与非法范围夹取
  pager.setPage(2);
  assert.equal(pager.currentPage.value, 2);
  pager.setPage(-10);
  assert.equal(pager.currentPage.value, 1);
  pager.setPage(999);
  assert.equal(pager.currentPage.value, 3);
});

test('PAGINATION-03: 数据缩减时的状态机自愈能力', async () => {
  const mockItems = ref(Array.from({ length: 50 }, (_, i) => ({ id: i + 1 })));
  const pager = usePagination(mockItems, { defaultPageSize: 10 });

  pager.setPage(5);
  assert.equal(pager.currentPage.value, 5);
  assert.equal(pager.totalPages.value, 5);

  // 模拟搜索过滤，数据骤降至 15 条（仅 2 页）
  mockItems.value = mockItems.value.slice(0, 15);
  await nextTick();

  assert.equal(pager.totalItems.value, 15);
  assert.equal(pager.totalPages.value, 2);
  // 必须自愈到最大合法页码 2，杜绝空白页
  assert.equal(pager.currentPage.value, 2);

  // 模拟过滤结果为空
  mockItems.value = [];
  await nextTick();
  assert.equal(pager.totalItems.value, 0);
  assert.equal(pager.totalPages.value, 1);
  assert.equal(pager.currentPage.value, 1);
  assert.equal(pager.startIndex.value, 0);
  assert.equal(pager.endIndex.value, 0);
  assert.equal(pager.pagedItems.value.length, 0);
});
