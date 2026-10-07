#!/usr/bin/env node
/**
 * LinuxWebTool 前端架构门禁（零依赖，node frontend-gate.cjs）
 *
 * 规则（详见 docs/dev/architecture-gates.md）：
 *  FE-HTML-INLINE     index.html 禁止内联脚本（importmap 除外）与 on* 内联事件
 *  FE-API-OWNERSHIP   API 路径字符串只允许出现在 config.js
 *  FE-NO-FETCH        fetch() 只允许出现在 api/client.js
 *  FE-STORAGE         localStorage / sessionStorage 只允许出现在 api/client.js 与 store/auth.js
 *  FE-IMPORT-BOUNDARY 跨层 import 限制（store→views 禁止、api→views/components 禁止、views 互引禁止）
 *  FE-TEMPLATE-REF    模板事件绑定必须使用内联调用（method()），避免运行时编译提升裸标识符导致 handler 丢失
 *  FE-API-METHOD      前端 HTTP 动词必须匹配 EndpointsMapper.g.cs 声明的后端路由
 *  FE-TAILWIND-CLASS  禁止使用非标 Tailwind 边距/尺寸类名（如 p-4.5、gap-4.5），防止 padding 归零导致元素贴边与圆角内容剪切
 *  FE-OVERFLOW-CONFLICT 同一元素禁止混用 truncate 与 overflow-(auto|scroll)，防止横向滚动失效导致子项省略/截断冲突
 *  FE-TEMPLATE-WINDOW Vue 模板禁止直接访问全局 window.*，必须通过 setup 显式 return 暴露
 *  FE-TEMPLATE-VALUE-REF Vue 模板禁止访问 setup 暴露变量的 .value（模板自动解包，写 .value 为反模式）
 *  FE-TEMPLATE-TAG-BALANCE Vue 组件模板关键结构标签（div/table/thead/tbody/section/aside）必须严格开闭平衡
 *  FE-TERMINAL-SESSION-HEAL TerminalView 必须具备失效会话自动重置与新建降级机制，防止服务重启后前端卡死
 *
 * 基线：frontend-gate-baseline.json 冻结存量债务，只允许删除条目，不允许新增。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const appDir = path.join(__dirname, 'app');
const baselineFile = path.join(__dirname, 'frontend-gate-baseline.json');

function walk(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'vendor' || entry.name === 'node_modules') continue;
      walk(full, files);
    } else if (/\.(js|html|css)$/.test(entry.name)) {
      files.push(full);
    }
  }
  return files;
}

function readBaseline() {
  try {
    return JSON.parse(fs.readFileSync(baselineFile, 'utf8')).violations || {};
  } catch {
    return {};
  }
}

function findCallEnd(source, start) {
  let depth = 0;
  let quote = null;
  for (let i = start; i < source.length; i++) {
    const char = source[i];
    const previous = source[i - 1];
    if (quote) {
      if (char === '\\') {
        i++;
      } else if (char === quote) {
        quote = null;
      }
      continue;
    }
    if (char === '\'' || char === '"' || char === '`') {
      quote = char;
      continue;
    }
    if (char === '/' && source[i + 1] === '/') {
      const newline = source.indexOf('\n', i);
      if (newline === -1) break;
      i = newline;
      continue;
    }
    if (char === '/' && source[i + 1] === '*') {
      const end = source.indexOf('*/', i + 2);
      if (end === -1) break;
      i = end + 1;
      continue;
    }
    if (char === '(') depth++;
    if (char === ')') {
      depth--;
      if (depth === 0) return i;
    }
  }
  return -1;
}

function normalizeRoute(pathValue) {
  return pathValue
    .replace(/^\/api(?=\/)/, '')
    .split('/')
    .filter(Boolean)
    .map((segment) => (segment.startsWith('{') || segment.startsWith(':') || /^%3a/i.test(segment) ? '*' : segment))
    .join('/');
}

function loadApiConfig(configFile) {
  const source = fs.readFileSync(configFile, 'utf8').replace(/\bexport\s+const\s+/g, 'const ');
  const sandbox = { window: { location: { origin: 'http://frontend-gate.local' } } };
  const result = new vm.Script(`${source}; ({ API });`).runInNewContext(sandbox);
  return result.API;
}

function resolveApiPath(api, expression) {
  const match = expression.match(/^API\.([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)/);
  if (!match) return null;

  let value = api;
  for (const segment of match[1].split('.')) {
    value = value?.[segment];
    if (value === undefined) return null;
  }
  if (typeof value === 'function') {
    value = value(...Array.from({ length: value.length }, () => ':param'));
  }
  return typeof value === 'string' ? value : null;
}

function loadBackendRouteTable(mapperFile) {
  const routes = new Map();
  let currentGroup = '';
  for (const line of fs.readFileSync(mapperFile, 'utf8').split(/\r?\n/)) {
    const group = line.match(/MapGroup\("([^"]+)"\)/);
    if (group) currentGroup = group[1];

    const endpoint = line.match(/\.Map(Get|Post|Put|Patch|Delete)\("([^"]*)"/);
    if (!endpoint) continue;

    const route = normalizeRoute(`${currentGroup}/${endpoint[2]}`);
    if (!routes.has(route)) routes.set(route, new Set());
    routes.get(route).add(endpoint[1].toUpperCase());
  }
  return routes;
}

const files = walk(appDir);
const violations = [];
const add = (rule, file, detail) => violations.push({ rule, file: path.relative(appDir, file).replace(/\\/g, '/'), detail });

for (const file of files) {
  const rel = path.relative(appDir, file).replace(/\\/g, '/');
  const content = fs.readFileSync(file, 'utf8');

  if (file.endsWith('.html')) {
    // FE-HTML-INLINE：除 importmap 外禁止无 src 的 script；禁止 on* 内联事件
    const scripts = content.matchAll(/<script\b([^>]*)>/g);
    for (const [, attrs] of scripts) {
      const isImportMap = /type\s*=\s*["']importmap["']/.test(attrs);
      const hasSrc = /\bsrc\s*=/.test(attrs);
      if (!isImportMap && !hasSrc) add('FE-HTML-INLINE', file, '存在内联 <script>（仅 importmap 允许）');
      if (/\bon[a-z]+\s*=/i.test(attrs)) add('FE-HTML-INLINE', file, '存在内联事件属性');
    }
    if (/\s(on[a-z]+)\s*=\s*["'][^"']*["']/i.test(content.replace(/<script[\s\S]*?<\/script>/g, ''))) {
      add('FE-HTML-INLINE', file, 'HTML 元素存在 on* 内联事件');
    }
    if (rel === 'index.html' && !/<base\s+href="\/app\/"/.test(content)) {
      add('FE-HTML-INLINE', file, 'index.html 缺少 <base href="/app/">');
    }
  }

  if (file.endsWith('.js')) {
    // FE-API-OWNERSHIP
    if (!rel.endsWith('config.js') && /["'`]\/api\//.test(content)) {
      add('FE-API-OWNERSHIP', file, 'API 路径字符串只能出现在 config.js');
    }
    // FE-NO-FETCH
    if (!rel.endsWith('client.js') && /\bfetch\s*\(/.test(content)) {
      add('FE-NO-FETCH', file, 'fetch() 只允许在 api/client.js 中调用');
    }
    // FE-STORAGE
    if (!['client.js', 'auth.js'].includes(path.basename(rel)) && /\blocalStorage\b|\bsessionStorage\b/.test(content)) {
      add('FE-STORAGE', file, 'Web Storage 只允许在 api/client.js 与 store/auth.js 中访问');
    }
    // FE-IMPORT-BOUNDARY
    const imports = [...content.matchAll(/import\s+(?:[\s\S]*?from\s+)?["'](\.[^"']+)["']/g)].map((m) => m[1]);
    for (const spec of imports) {
      const target = path.normalize(path.join(path.dirname(file), spec)).replace(/\\/g, '/');
      const targetRel = path.relative(appDir, target).replace(/\\/g, '/');
      if (rel.startsWith('store/') && /\/(views|components)\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `store 禁止 import ${targetRel}`);
      }
      if (rel.startsWith('api/') && /\/(views|components)\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `api 禁止 import ${targetRel}`);
      }
      if (rel.startsWith('views/') && targetRel.startsWith('views/') && targetRel !== rel) {
        add('FE-IMPORT-BOUNDARY', file, `views 禁止互相 import（${targetRel}）`);
      }
      if (rel.startsWith('components/') && /\/views\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `components 禁止 import views（${targetRel}）`);
      }
    }
    // FE-TEMPLATE-REF：模板事件绑定禁止裸标识符（运行时编译会错误提升导致 handler 丢失）
    const bare = content.matchAll(/@(?:click|blur|keyup(?:\.[a-z]+)*|submit(?:\.[a-z]+)*|change)\s*=\s*"([A-Za-z_$][\w$]*)"/g);
    for (const [, name] of bare) {
      add('FE-TEMPLATE-REF', file, `事件绑定使用了裸标识符 "${name}"，必须写成 "${name}()"`);
    }

    // FE-TAILWIND-CLASS：禁止使用未被默认 spacing 缩放支持的非标浮点类（例如 p-4.5、m-4.5 等，会导致样式失效为 0）
    const invalidTailwind = content.match(/\b(?:p|m|px|py|pt|pb|pl|pr|gap|w|h)-4\.5\b/g);
    if (invalidTailwind) {
      add('FE-TAILWIND-CLASS', file, `存在无效非标 Tailwind 类名 "${invalidTailwind[0]}"，请使用标准阶梯（如 p-4, p-5）或自定义方括号尺寸`);
    }

    // FE-OVERFLOW-CONFLICT：同一 class 属性禁止混用 truncate 与 overflow-(auto|scroll)
    const classMatches = content.matchAll(/class\s*=\s*["']([^"']*)["']/g);
    for (const [, cls] of classMatches) {
      if (/\btruncate\b/.test(cls) && /\boverflow-(?:x-|y-)?(?:auto|scroll)\b/.test(cls)) {
        add('FE-OVERFLOW-CONFLICT', file, `同一元素混用了 truncate 与 overflow-auto/scroll，两者 CSS 冲突导致横向滚动失效`);
      }
    }

    // 针对 Vue 组件 template: `...` 的静态模板检查
    const tplMatch = content.match(/template:\s*`([\s\S]*?)`/);
    if (tplMatch) {
      const tpl = tplMatch[1];

      // FE-TEMPLATE-WINDOW：模板禁止直接访问全局 window.*
      if (/\{\{[^}]*\bwindow\./.test(tpl) || /:[\w-]+\s*=\s*["'][^"']*\bwindow\./.test(tpl)) {
        add('FE-TEMPLATE-WINDOW', file, 'Vue 模板中禁止直接访问 window.*（Vue 模板沙箱无全局 window，运行时报错），请在 setup 中暴露');
      }

      // FE-TEMPLATE-VALUE-REF：模板禁止显式访问 setup 暴露变量的 .value（模板自动解包）
      const setupReturnMatch = content.match(/return\s*\{([\s\S]*?)\};?\s*\},?\s*(?:template|mounted)/);
      if (setupReturnMatch) {
        const returnedVars = setupReturnMatch[1]
          .split(',')
          .map((s) => s.trim().split(':')[0].trim())
          .filter((s) => /^[a-zA-Z_$][\w$]*$/.test(s) && !['formatTime', 'formatBytes', 'formatBps', 'formatDuration'].includes(s));

        if (returnedVars.length) {
          const varPattern = new RegExp(`\\b(${returnedVars.join('|')})\\.value\\b`);
          const valMatch = tpl.match(varPattern);
          if (valMatch) {
            add('FE-TEMPLATE-VALUE-REF', file, `模板中错误访问了 ref/computed 的 .value（"${valMatch[0]}"），Vue 模板已自动解包`);
          }
        }
      }

      // FE-TEMPLATE-TAG-BALANCE：Vue 组件模板关键标签必须开闭平衡
      const checkTags = ['div', 'table', 'thead', 'tbody', 'section', 'aside'];
      for (const tag of checkTags) {
        const openRegex = new RegExp(`<${tag}\\b[^>]*>`, 'gi');
        const closeRegex = new RegExp(`</${tag}>`, 'gi');
        const opens = (tpl.match(openRegex) || []).length;
        const closes = (tpl.match(closeRegex) || []).length;
        if (opens !== closes) {
          add('FE-TEMPLATE-TAG-BALANCE', file, `<${tag}> 标签不平衡: ${opens} 处开启 vs ${closes} 处闭合，可能导致 DOM 树结构异常`);
        }
      }
    }

    // FE-TERMINAL-SESSION-HEAL：TerminalView 必须具备失效会话自动重置与新建降级机制，防止服务重启后前端卡死
    if (path.basename(file) === 'TerminalView.js') {
      if (!content.includes('API.terminal.sessions') || !content.includes("runtime.sessionId = ''")) {
        add('FE-TERMINAL-SESSION-HEAL', file, 'TerminalView 必须具备失效会话自动重置与新建降级机制，防止服务重启后前端卡死');
      }
    }
  }
}

// FE-API-METHOD：以前端 config 为调用点索引、后端 mapper 为契约，防止 GET 默认值误调写接口。
const configFile = path.join(__dirname, 'app', 'config.js');
const mapperFile = path.join(__dirname, '..', 'MinimalApi', 'EndpointsMapper.g.cs');
try {
  const api = loadApiConfig(configFile);
  const backendRoutes = loadBackendRouteTable(mapperFile);

  for (const file of files) {
    if (!file.endsWith('.js') || path.relative(appDir, file).replace(/\\/g, '/') === 'api/client.js') continue;
    const content = fs.readFileSync(file, 'utf8');
    const callPattern = /\b(httpUpload|httpDownload|http)\s*\(/g;
    let callMatch;
    while ((callMatch = callPattern.exec(content))) {
      const openParen = callMatch.index + callMatch[0].length - 1;
      const endParen = findCallEnd(content, openParen);
      if (endParen === -1) continue;

      const call = content.slice(callMatch.index, endParen + 1);
      const apiExpression = call.slice(callMatch[0].length).match(/^API\.[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*/);
      if (!apiExpression) continue;

      const configuredPath = resolveApiPath(api, apiExpression[0]);
      if (!configuredPath) {
        add('FE-API-METHOD', file, `无法解析 API 路径：${apiExpression[0]}`);
        continue;
      }

      const explicitMethod = call.match(/\bmethod\s*:\s*(['"])([A-Za-z]+)\1/)?.[2];
      const actualMethod = callMatch[1] === 'httpUpload'
        ? 'POST'
        : callMatch[1] === 'httpDownload'
          ? 'GET'
          : (explicitMethod || 'GET').toUpperCase();
      const route = normalizeRoute(configuredPath);
      const allowedMethods = backendRoutes.get(route);

      if (!allowedMethods) {
        add('FE-API-METHOD', file, `${configuredPath} 在后端路由表中不存在`);
      } else if (!allowedMethods.has(actualMethod)) {
        add('FE-API-METHOD', file, `${actualMethod} ${configuredPath} 不匹配后端 ${[...allowedMethods].sort().join('/')} ${configuredPath}`);
      } else if (allowedMethods.size > 1 && !explicitMethod) {
        add('FE-API-METHOD', file, `${configuredPath} 支持多个方法（${[...allowedMethods].sort().join('/')}），必须显式声明 method`);
      }
    }
  }
} catch (error) {
  add('FE-API-METHOD', configFile, `契约检查失败：${error.message}`);
}

// 基线比对
const baseline = readBaseline();
const baselineSet = new Set();
for (const [rule, entries] of Object.entries(baseline)) {
  for (const entry of entries) baselineSet.add(`${rule}|${entry}`);
}
const fresh = violations.filter((v) => !baselineSet.has(`${v.rule}|${v.file}:${v.detail}`));
const unusedBaseline = [...baselineSet].filter((key) => {
  const [rule, rest] = key.split('|');
  const idx = rest.lastIndexOf(':');
  return !violations.some((v) => v.rule === rule && `${v.file}:${v.detail}` === rest);
});

console.log(`前端架构门禁：扫描 ${files.length} 个文件`);
for (const v of fresh) {
  console.error(`  [${v.rule}] ${v.file} — ${v.detail}`);
}
if (fresh.length > 0) {
  console.error(`\n❌ 前端门禁失败：${fresh.length} 个新违规。修复依赖，不得扩充基线。`);
  process.exit(1);
}
if (unusedBaseline.length > 0) {
  console.warn(`⚠ 基线中 ${unusedBaseline.length} 条已失效，请从 frontend-gate-baseline.json 删除（基线只减不增）：`);
  for (const key of unusedBaseline) console.warn(`   - ${key}`);
}
console.log('✅ 前端架构门禁通过');
