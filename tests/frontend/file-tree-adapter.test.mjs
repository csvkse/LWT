import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';
import * as vue from '../../src/LinuxWebTool.WebHost/wwwroot/app/vendor/vue.esm-browser.prod.js';

const fileTreeUrl = new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js', import.meta.url);

async function loadFileTreeModule() {
  const context = vm.createContext({});
  const mod = new vm.SourceTextModule(await readFile(fileTreeUrl, 'utf8'), { context });
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

const { buildFileTree, flattenFileTree, getAncestorDirectories, useFileTree, getFileIcon } = await loadFileTreeModule();
const { ref, nextTick } = vue;

test('FILE-TREE-01: 基础多叉树构建与 Folders-First 排序正确', () => {
  const rawFiles = [
    'src/index.js',
    'manifest.json',
    'package.json',
    'src/handlers/action.js',
    'src/handlers/event.js',
    'src/utils/format.js',
    'README.md',
  ];

  const tree = buildFileTree(rawFiles);
  assert.equal(tree.isDir, true);
  assert.ok(tree.sortedChildren.length > 0);

  // 根层级目录优先：src 目录应该排在 README.md, manifest.json 之前
  const rootFirst = tree.sortedChildren[0];
  assert.equal(rootFirst.name, 'src');
  assert.equal(rootFirst.isDir, true);

  // src 目录下包含 handlers 目录、utils 目录以及 index.js
  const srcNode = rootFirst;
  assert.equal(srcNode.sortedChildren[0].name, 'handlers');
  assert.equal(srcNode.sortedChildren[1].name, 'utils');
  assert.equal(srcNode.sortedChildren[2].name, 'index.js');
  assert.equal(srcNode.sortedChildren[2].isDir, false);
});

test('FILE-TREE-02: 祖先目录链条解析准确', () => {
  assert.deepEqual(Array.from(getAncestorDirectories('manifest.json')), []);
  assert.deepEqual(Array.from(getAncestorDirectories('src/index.js')), ['src']);
  assert.deepEqual(Array.from(getAncestorDirectories('src/handlers/action.js')), ['src', 'src/handlers']);
  assert.deepEqual(Array.from(getAncestorDirectories('web/dist/assets/index.css')), ['web', 'web/dist', 'web/dist/assets']);
});

test('FILE-TREE-03: 智能聚焦与折叠状态机流转', async () => {
  const filesRef = ref([
    'manifest.json',
    'src/index.js',
    'src/handlers/action.js',
    'src/handlers/event.js',
    'web/index.html',
  ]);
  const activeFileRef = ref('src/handlers/action.js');

  const { visibleNodes, toggleFolder, expandAll, collapseAll, expandedFolders } = useFileTree(filesRef, activeFileRef);

  // 初始状态：activeFile 为 src/handlers/action.js，其祖先 'src' 和 'src/handlers' 自动展开
  assert.ok(expandedFolders.value.has('src'), 'src 目录应当自动展开');
  assert.ok(expandedFolders.value.has('src/handlers'), 'src/handlers 目录应当自动展开');
  assert.ok(!expandedFolders.value.has('web'), '非活动目录 web 应当默认折叠');

  // 检查拍平节点可见性
  const visiblePaths = visibleNodes.value.map((n) => n.fullPath);
  assert.ok(visiblePaths.includes('src/handlers/action.js'), '活动文件应当可见');
  assert.ok(visiblePaths.includes('src/handlers/event.js'), '同目录兄弟文件应当可见');
  assert.ok(!visiblePaths.includes('web/index.html'), '未展开的 web 目录下文件应当隐藏');

  // 手动收起 src 目录
  toggleFolder('src');
  await nextTick();
  assert.ok(!expandedFolders.value.has('src'));
  const collapsedPaths = visibleNodes.value.map((n) => n.fullPath);
  assert.ok(!collapsedPaths.includes('src/handlers/action.js'), '收起父目录后子项隐藏');

  // 全部展开测试
  expandAll();
  await nextTick();
  const allPaths = visibleNodes.value.map((n) => n.fullPath);
  assert.ok(allPaths.includes('web/index.html'), '全部展开后 web/index.html 可见');

  // 全部收折测试 (保留当前活动文件分支)
  collapseAll();
  await nextTick();
  assert.ok(expandedFolders.value.has('src/handlers'), '收折后应保持当前活动文件聚焦');
});

test('FILE-TREE-04: 图标策略映射准确', () => {
  assert.equal(getFileIcon('folder', true, false), '📁');
  assert.equal(getFileIcon('folder', true, true), '📂');
  assert.equal(getFileIcon('index.js', false, false), '🟨');
  assert.equal(getFileIcon('manifest.json', false, false), '🟩');
  assert.equal(getFileIcon('index.html', false, false), '🟧');
  assert.equal(getFileIcon('style.css', false, false), '🟦');
  assert.equal(getFileIcon('README.md', false, false), '📝');
  assert.equal(getFileIcon('run.sh', false, false), '⚡');
  assert.equal(getFileIcon('file.bak', false, false), '📦');
  assert.equal(getFileIcon('other.bin', false, false), '📄');
});
