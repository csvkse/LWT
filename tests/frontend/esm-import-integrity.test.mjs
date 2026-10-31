import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

const appDir = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app');

function walk(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'vendor' || entry.name === 'node_modules') continue;
      walk(full, files);
    } else if (entry.name.endsWith('.js')) {
      files.push(full);
    }
  }
  return files;
}

const exportCache = new Map();
function getExportsOfFile(filePath) {
  if (exportCache.has(filePath)) return exportCache.get(filePath);
  const exported = new Set();
  if (!fs.existsSync(filePath)) {
    exportCache.set(filePath, exported);
    return exported;
  }
  const code = fs.readFileSync(filePath, 'utf8');
  // 1. export function / class / const / let / var
  const declMatches = code.matchAll(/export\s+(?:async\s+)?(?:function\*?|class|const|let|var)\s+([A-Za-z0-9_$]+)/g);
  for (const [, name] of declMatches) exported.add(name);

  // 2. export { a, b as c, d }
  const blockMatches = code.matchAll(/export\s*\{([\s\S]*?)\}(?:\s*from\s*["'][^"']+["'])?/g);
  for (const [, block] of blockMatches) {
    const items = block.split(',');
    for (let item of items) {
      item = item.trim();
      if (!item) continue;
      const parts = item.split(/\s+as\s+/);
      const exportedName = parts[parts.length - 1].trim();
      if (exportedName) exported.add(exportedName);
    }
  }

  // 3. export default
  if (/export\s+default\b/.test(code)) {
    exported.add('default');
  }

  exportCache.set(filePath, exported);
  return exported;
}

test('FE-ESM-01: 全量前端业务组件与视图的 ESM 相对路径导入符号必须真实导出 (零未导出导入)', () => {
  const files = walk(appDir);
  assert.ok(files.length >= 40, `预期扫描到至少 40 个前端 JS 文件，实际扫描到 ${files.length} 个`);

  const missingExportErrors = [];

  for (const file of files) {
    const content = fs.readFileSync(file, 'utf8');
    const relFile = path.relative(appDir, file).replace(/\\/g, '/');

    // 校验命名导入: import { a, b } from './xxx.js'
    const namedImportMatches = content.matchAll(/import\s*\{([^{};]*?)\}\s*from\s*["'](\.[^"']+)["']/g);
    for (const [, namesBlock, spec] of namedImportMatches) {
      const target = path.normalize(path.join(path.dirname(file), spec));
      if (!fs.existsSync(target)) {
        missingExportErrors.push({ file: relFile, target: spec, symbol: '(file not found)', message: `导入的文件不存在: ${spec}` });
        continue;
      }
      const targetExports = getExportsOfFile(target);
      const rawNames = namesBlock.split(',');
      for (let rawName of rawNames) {
        rawName = rawName.trim();
        if (!rawName) continue;
        const originalName = rawName.split(/\s+as\s+/)[0].trim();
        if (originalName && !targetExports.has(originalName)) {
          missingExportErrors.push({
            file: relFile,
            target: spec,
            symbol: originalName,
            message: `模块 ${relFile} 从 ${spec} 导入了符号 "${originalName}"，但目标模块未导出该符号`,
          });
        }
      }
    }

    // 校验默认导入: import DefaultName from './xxx.js'
    const defaultImportMatches = content.matchAll(/import\s+([A-Za-z0-9_$]+)\s+from\s*["'](\.[^"']+)["']/g);
    for (const [, defaultName, spec] of defaultImportMatches) {
      if (defaultName === 'type' || defaultName === '{') continue;
      const target = path.normalize(path.join(path.dirname(file), spec));
      if (fs.existsSync(target)) {
        const targetExports = getExportsOfFile(target);
        if (!targetExports.has('default')) {
          missingExportErrors.push({
            file: relFile,
            target: spec,
            symbol: 'default',
            message: `模块 ${relFile} 试图默认导入 "${defaultName}"，但 ${spec} 未提供 export default`,
          });
        }
      }
    }
  }

  if (missingExportErrors.length > 0) {
    console.error('ESM 导入完整性校验失败列表:', JSON.stringify(missingExportErrors, null, 2));
  }
  assert.equal(missingExportErrors.length, 0, `发现 ${missingExportErrors.length} 处 ESM 导入未导出符号违规，详见控制台输出`);
});
