#!/usr/bin/env node
/**
 * LinuxWebTool (LWT) 凭据安全检测门禁与增量提交流水线守卫
 * 采用策略模式与香农熵算法，拦截真实密钥、未脱敏密码、隧道 ID 及内部地址
 * 支持模式：
 *   node .githooks/security-gate.cjs --staged   (增量扫描暂存区变更，< 80ms)
 *   node .githooks/security-gate.cjs --full     (全仓非忽略文件扫描，架构测试与 CI 兜底)
 */

const fs = require('node:fs');
const path = require('node:path');
const { execSync } = require('node:child_process');

// 颜色输出辅助
const colors = {
  reset: '\x1b[0m',
  red: '\x1b[31m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  cyan: '\x1b[36m',
  bold: '\x1b[1m'
};

const ROOT = path.resolve(__dirname, '..');
const BASELINE_FILE = path.join(__dirname, 'security-gate-baseline.json');

// 香农熵计算 (度量随机性)
function shannonEntropy(str) {
  if (!str) return 0;
  const len = str.length;
  const freqs = {};
  for (let i = 0; i < len; i++) {
    freqs[str[i]] = (freqs[str[i]] || 0) + 1;
  }
  let entropy = 0;
  for (const char in freqs) {
    const p = freqs[char] / len;
    entropy -= p * Math.log2(p);
  }
  return entropy;
}

// 归一化度量熵计算
function metricEntropy(str, charsetSize) {
  const ent = shannonEntropy(str);
  const maxEnt = Math.log2(charsetSize);
  return maxEnt > 0 ? ent / maxEnt : 0;
}

// 常见 Mock / 测试词典
const MOCK_LEXICON = /mock|test|dummy|sample|example|placeholder|super-safe|super_sensitive|changeme|default|existing_header|your[-_]?key|123456/i;

// IPv4 地址分类合规性引擎 (RFC 1918 / RFC 5737 / RFC 6598 / Loopback)
function isSafeIPv4(ip) {
  const parts = ip.split('.').map(p => parseInt(p, 10));
  if (parts.length !== 4 || parts.some(p => isNaN(p) || p < 0 || p > 255)) return true;
  const [a, b, c, d] = parts;
  if (a === 127) return true; // Loopback
  if (a === 0 || (a === 255 && b === 255 && c === 255 && d === 255)) return true; // 0.0.0.0, 255.255.255.255
  if (a === 10) return true; // RFC 1918
  if (a === 172 && b >= 16 && b <= 31) return true; // RFC 1918
  if (a === 192 && b === 168) return true; // RFC 1918
  if (a === 192 && b === 0 && c === 2) return true; // RFC 5737 TEST-NET-1
  if (a === 198 && b === 51 && c === 100) return true; // RFC 5737 TEST-NET-2
  if (a === 203 && b === 0 && c === 113) return true; // RFC 5737 TEST-NET-3
  if (a === 169 && b === 254) return true; // RFC 3927 Link-Local
  if (a === 198 && (b === 18 || b === 19)) return true; // RFC 2544 Benchmark
  if (a === 100 && b >= 64 && b <= 127) return true; // RFC 6598 CGNAT / Shared Address Space
  if (ip === '8.8.8.8' || ip === '8.8.4.4' || ip === '1.1.1.1' || ip === '1.2.3.4') return true; // Public DNS / Test Stubs
  return false;
}

// 1. 策略模式：检测规则策略集
const RULES = [
  {
    id: 'SEC-KNOWN-LEAKS',
    name: '已知历史泄露字串防复发',
    description: '严禁已排查脱敏的历史密码、隧道 ID、内部地址复发入库',
    evaluate(line) {
      const knownLeaks = [
        'uFkTJxVyP5ahYnyj',
        'fnnas.hkadmin',
        '333123.xyz',
        'tunnel_6ae63d1c1308819195a4aa8ed780e951'
      ];
      for (const leak of knownLeaks) {
        if (line.includes(leak)) {
          return { match: leak, message: `命中已知历史泄密字串: ${leak}` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-TUNNEL-ID',
    name: '真实隧道 ID 防泄露',
    description: 'Tunnel ID 必须使用标准虚拟占位符',
    evaluate(line) {
      const re = /\btunnel_([a-z0-9]{4}_)?[a-z0-9]{32}\b/g;
      let m;
      while ((m = re.exec(line)) !== null) {
        const val = m[0];
        // 允许合规的占位符
        if (val === 'tunnel_0123456789abcdef0123456789abcdef' ||
            val === 'tunnel_12345678901234567890123456789012' ||
            val === 'tunnel_00000000000000000000000000000000') {
          continue;
        }
        return { match: val, message: `非法或疑似真实 Tunnel ID: ${val} (请使用 tunnel_0123456789abcdef0123456789abcdef)` };
      }
      return null;
    }
  },
  {
    id: 'SEC-OPENAI-KEY',
    name: 'OpenAI / LLM API Key 防泄露',
    description: '拦截真实 OpenAI/LLM Key',
    evaluate(line) {
      const re = /\bsk-[a-zA-Z0-9_\-]{20,}\b/g;
      let m;
      while ((m = re.exec(line)) !== null) {
        const val = m[0];
        if (MOCK_LEXICON.test(val) || val.startsWith('sk-proj-...')) {
          continue;
        }
        return { match: val, message: `检测到疑似真实 LLM API Key: ${val}` };
      }
      return null;
    }
  },
  {
    id: 'SEC-PRIVATE-KEY',
    name: '私钥物理块防泄露',
    description: '源码与文档中禁止包含真实 PRIVATE KEY',
    evaluate(line) {
      if (line.includes('-----BEGIN') && line.includes('PRIVATE KEY-----')) {
        // 若处于 placeholder 属性或包含明确的单行掩码占位符
        if (line.includes('placeholder=') || line.includes(':placeholder=') ||
            line.includes('...') || line.includes('MOCK_KEY') ||
            line.includes('test-key-content') || line.includes('TEST_KEY')) {
          return null;
        }
        return { match: line.trim(), message: '检测到未掩码的私钥头部声明' };
      }
      return null;
    }
  },
  {
    id: 'SEC-ADMIN-PASSWORD',
    name: '未脱敏管理员明文密码防泄露',
    description: '禁止在配置文件或脚本中硬编码高熵密码',
    evaluate(line) {
      const re = /\b(?:web_admin_password|admin_password)\b\s*[:=]\s*["']?([^\s"'`#;,]+)["']?/i;
      const m = re.exec(line);
      if (m) {
        const val = m[1].trim();
        if (!val || val.includes('...') || val.includes('***') ||
            /^(?:your_|default|test|mock|dummy|placeholder|<|\$|Guid|process\.env|""|''|string)/i.test(val)) {
          return null;
        }
        if (val.length >= 6) {
          return { match: val, message: `检测到未脱敏的管理员密码定义: ${val} (请替换为 your_secure_password_here)` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-EASYTIER-SECRET',
    name: 'EasyTier 组网凭据防泄露',
    description: '禁止在 TOML 或配置中硬编码实际组网密钥',
    evaluate(line) {
      const re = /\bnetwork_secret\b\s*[:=]\s*["']([^"']+)["']/i;
      const m = re.exec(line);
      if (m) {
        const val = m[1].trim();
        if (!val || val.includes('...') || val.includes('***') ||
            /^(?:demo_|test_|mock_|super_sensitive|\$|entity\.NetworkSecret|""|'')[^"']*$/i.test(val)) {
          return null;
        }
        return { match: val, message: `检测到疑似真实 EasyTier 组网密钥: ${val}` };
      }
      return null;
    }
  },
  {
    id: 'SEC-HIGH-ENTROPY-SECRET',
    name: '高熵可疑敏感字段防泄露',
    description: '度量未知变量赋值的香农熵',
    evaluate(line) {
      const re = /(?:api_?key|auth_?token|preshared_?key|secret_?key)\s*[:=]\s*["']([^"']{16,})["']/gi;
      let m;
      while ((m = re.exec(line)) !== null) {
        const val = m[1];
        if (MOCK_LEXICON.test(val) || val.includes('${') || val.includes('...')) {
          continue;
        }
        const ent = shannonEntropy(val);
        if (ent > 3.65 && !/^[0-9a-fA-F-]+$/.test(val)) { // 排除纯 Guid/Hex
          return { match: val, message: `高熵可疑凭据 (熵值: ${ent.toFixed(2)}): ${val}` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-ENTROPY-BASE64',
    name: '多字符集 Base64 / JWT / Bearer 高熵 Token 防泄露',
    description: '通过自适应度量熵拦截未经脱敏的 JWT、Bearer 与高熵 Base64 凭据',
    evaluate(line) {
      if (MOCK_LEXICON.test(line)) return null;
      // 1. JWT 深度识别
      const jwtRe = /\beyJ[a-zA-Z0-9_-]{10,}\.eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\b/g;
      let m = jwtRe.exec(line);
      if (m) {
        return { match: m[0], message: `检测到疑似真实 JWT 凭据: ${m[0].substring(0, 20)}...` };
      }
      // 2. Bearer Token 识别
      const bearerRe = /\bBearer\s+([a-zA-Z0-9_\-\.]{24,})\b/g;
      while ((m = bearerRe.exec(line)) !== null) {
        const val = m[1];
        if (MOCK_LEXICON.test(val) || val.includes('...')) continue;
        const ent = shannonEntropy(val);
        if (ent > 4.55) {
          return { match: val, message: `检测到高熵 Bearer Token (熵值: ${ent.toFixed(2)}): ${val.substring(0, 16)}...` };
        }
      }
      // 3. 赋值语句高熵 Base64
      const assignRe = /(?:secret|token|credential|api[-_]?key|private[-_]?key)\s*[:=]\s*["']?([A-Za-z0-9+/=_-]{24,})["']?/gi;
      while ((m = assignRe.exec(line)) !== null) {
        const val = m[1];
        if (MOCK_LEXICON.test(val) || val.includes('${') || val.includes('...')) continue;
        const ent = shannonEntropy(val);
        const metric = metricEntropy(val, 64);
        if (ent > 4.55 && metric > 0.75) {
          return { match: val, message: `检测到高熵 Base64 敏感参数 (熵: ${ent.toFixed(2)}, Metric: ${metric.toFixed(2)}): ${val.substring(0, 16)}...` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-ENTROPY-HEX',
    name: '32/64 位十六进制高熵私钥/哈希防泄露',
    description: '通过字符集归一化度量熵拦截十六进制私钥与随机哈希',
    evaluate(line) {
      if (MOCK_LEXICON.test(line)) return null;
      const hexRe = /(?:secret|key|token|auth|hash|hmac)\s*[:=]\s*["']?([0-9a-fA-F]{32,64})["']?/gi;
      let m;
      while ((m = hexRe.exec(line)) !== null) {
        const val = m[1];
        if (MOCK_LEXICON.test(val) || val.includes('...')) continue;
        if (/^(.)\1+$/.test(val) || val.toLowerCase().includes('0123456789abcdef')) continue;
        const ent = shannonEntropy(val);
        const metric = metricEntropy(val, 16);
        if (ent > 3.25 && metric > 0.81) {
          return { match: val, message: `检测到高熵 Hex 密钥/哈希 (熵: ${ent.toFixed(2)}, Metric: ${metric.toFixed(2)}): ${val.substring(0, 16)}...` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-PSEUDO-MASKING',
    name: '假脱敏/危险半脱敏模式防泄露',
    description: '拦截带有掩码符号但依然泄露高熵随机前缀的假脱敏内容',
    evaluate(line) {
      if (!line.includes('...') && !line.includes('***')) return null;
      const skMatch = /\bsk-[a-zA-Z0-9_\-]{12,}(?:\.\.\.|\*\*\*)/.exec(line);
      if (skMatch && !MOCK_LEXICON.test(skMatch[0])) {
        return { match: skMatch[0], message: `检测到危险的半脱敏 OpenAI 密钥: ${skMatch[0]}` };
      }
      const tunnelMatch = /\btunnel_[a-z0-9]{10,}(?:\.\.\.|\*\*\*)/.exec(line);
      if (tunnelMatch && !MOCK_LEXICON.test(tunnelMatch[0])) {
        return { match: tunnelMatch[0], message: `检测到危险的半脱敏 Tunnel ID: ${tunnelMatch[0]}` };
      }
      const pwdMatch = /\b(?:web_admin_password|admin_password)\s*[:=]\s*["']?([^\s"'`#;,]{6,})(?:\.\.\.|\*\*\*)/i.exec(line);
      if (pwdMatch) {
        const val = pwdMatch[1];
        if (!MOCK_LEXICON.test(val) && !val.startsWith('your_')) {
          return { match: pwdMatch[0], message: `检测到危险的半脱敏密码暴露: ${pwdMatch[0]}` };
        }
      }
      return null;
    }
  },
  {
    id: 'SEC-UNMASKED-NETWORK-ADDRESS',
    name: '公网真实 IPv4 与非标动态域名防泄露',
    description: '校验网络地址上下文中的 IPv4 (须符合 RFC 5737 测试保留或 RFC 1918 私网) 与非标动态域名',
    evaluate(line) {
      const isNetContext = /(?:https?:\/\/|tcp:\/\/|udp:\/\/|\bhost\s*[:=]|\bip\s*[:=]|\bserver\s*[:=]|\bpeer\s*[:=])/i.test(line);
      if (!isNetContext) return null;
      if (MOCK_LEXICON.test(line)) return null;

      const ipRe = /\b([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})\b/g;
      let m;
      while ((m = ipRe.exec(line)) !== null) {
        const ip = m[1];
        if (!isSafeIPv4(ip)) {
          return { match: ip, message: `检测到未脱敏的公网 IPv4 地址: ${ip} (请改用 RFC 5737 示例 IP 如 203.0.113.1 或 127.0.0.1)` };
        }
      }

      const domainRe = /\b([a-zA-Z0-9_\-]+\.(?:xyz|vip|dynv6\.net|f3322\.net|duckdns\.org|no-ip\.com))\b/gi;
      while ((m = domainRe.exec(line)) !== null) {
        const domain = m[1];
        if (domain.includes('example.com') || domain.includes('public.easytier.top') || domain.includes('ghfast.top')) continue;
        return { match: domain, message: `检测到疑似未脱敏的私有动态域名: ${domain} (请改用 example.com 或 easytier-peer.example.com)` };
      }
      return null;
    }
  }
];

// 2. 基线白名单加载
function loadBaseline() {
  if (!fs.existsSync(BASELINE_FILE)) return { exemptions: [] };
  try {
    return JSON.parse(fs.readFileSync(BASELINE_FILE, 'utf-8'));
  } catch (err) {
    console.warn('⚠️ 警告: 读取 security-gate-baseline.json 失败:', err.message);
    return { exemptions: [] };
  }
}

// 二进制文件后缀快速排除
const BINARY_EXTS = new Set([
  '.png', '.jpg', '.jpeg', '.gif', '.ico', '.svg', '.webp',
  '.dll', '.exe', '.so', '.dylib', '.bin', '.tar', '.gz', '.zip',
  '.woff', '.woff2', '.ttf', '.eot', '.wasm', '.pdb', '.o', '.a'
]);

// 3. 适配器模式：数据源提取
function getCandidateFiles(isStaged) {
  try {
    if (isStaged) {
      // 仅提取暂存区新增、复制、修改的文件
      const out = execSync('git diff --cached --name-only --diff-filter=ACMR', { encoding: 'utf-8', cwd: ROOT });
      return out.split(/\r?\n/).map(s => s.trim()).filter(Boolean);
    } else {
      // 全仓受控文件 + 未忽略新文件
      const t = execSync('git ls-files', { encoding: 'utf-8', cwd: ROOT });
      const u = execSync('git ls-files --others --exclude-standard', { encoding: 'utf-8', cwd: ROOT });
      const set = new Set([...t.split(/\r?\n/), ...u.split(/\r?\n/)]);
      return Array.from(set).map(s => s.trim()).filter(Boolean);
    }
  } catch (e) {
    console.error('Git 执行失败:', e.message);
    return [];
  }
}

function shouldSkipPath(relPath) {
  const norm = relPath.replace(/\\/g, '/');
  // 跳过自身与专属隔离目录
  if (norm.startsWith('docs_secret/') ||
      norm.startsWith('.githooks/') ||
      norm.startsWith('.git/') ||
      norm.startsWith('node_modules/') ||
      norm.includes('/bin/') ||
      norm.includes('/obj/') ||
      norm === 'secret_scan_results.txt') {
    return true;
  }
  const ext = path.extname(norm).toLowerCase();
  if (BINARY_EXTS.has(ext)) return true;
  return false;
}

// 4. 扫描核心引擎
function runSecurityGate() {
  const isStaged = process.argv.includes('--staged');
  const startTime = Date.now();
  const baseline = loadBaseline();
  const baselineHits = new Set();

  console.log(`${colors.cyan}${colors.bold}🔍 [Security Gate] 启动安全凭据扫描模式: ${isStaged ? '【暂存区增量】' : '【全仓受控】'}${colors.reset}`);

  let violationCount = 0;
  let scannedFiles = 0;

  function checkLine(normPath, lineNo, content) {
    for (const rule of RULES) {
      const result = rule.evaluate(content);
      if (result) {
        // 检查基线豁免
        const isExempt = (baseline.exemptions || []).some((ex, exIdx) => {
          if (ex.file === normPath && ex.rule === rule.id) {
            baselineHits.add(exIdx);
            return true;
          }
          return false;
        });

        if (isExempt) continue;

        violationCount++;
        console.error(`\n${colors.red}${colors.bold}❌ [${rule.id}] ${rule.name}${colors.reset}`);
        console.error(`   ${colors.yellow}位置: ${colors.bold}${normPath}:${lineNo}${colors.reset}`);
        console.error(`   ${colors.red}提示: ${result.message}${colors.reset}`);
        console.error(`   ${colors.cyan}内容: ${content.trim()}${colors.reset}`);
      }
    }
  }

  if (isStaged) {
    // 单进程提取全量暂存区差异流 (毫秒级极速单次调用)
    let diffOut = '';
    try {
      diffOut = execSync('git diff --cached -U0', { encoding: 'utf-8', cwd: ROOT, maxBuffer: 32 * 1024 * 1024 });
    } catch (e) {
      console.error('Git diff 失败:', e.message);
      process.exit(1);
    }

    const diffLines = diffOut.split(/\r?\n/);
    let currentFile = null;
    let currentLineNo = 0;
    const filesMap = new Map();

    for (const dl of diffLines) {
      if (dl.startsWith('diff --git ')) {
        const parts = dl.split(' ');
        let bPath = parts[parts.length - 1];
        if (bPath.startsWith('b/')) bPath = bPath.substring(2);
        currentFile = bPath.replace(/\\/g, '/');
        currentLineNo = 0;
      } else if (dl.startsWith('@@ ')) {
        const m = /\+([0-9]+)/.exec(dl);
        if (m) currentLineNo = parseInt(m[1], 10) - 1;
      } else if (dl.startsWith('+') && !dl.startsWith('+++')) {
        currentLineNo++;
        if (currentFile && !shouldSkipPath(currentFile)) {
          if (!filesMap.has(currentFile)) filesMap.set(currentFile, []);
          filesMap.get(currentFile).push({ lineNo: currentLineNo, content: dl.substring(1) });
        }
      }
    }

    scannedFiles = filesMap.size;
    for (const [normPath, lines] of filesMap) {
      for (const { lineNo, content } of lines) {
        checkLine(normPath, lineNo, content);
      }
    }
  } else {
    // 全仓非忽略文件扫描
    const files = getCandidateFiles(false);
    for (const relPath of files) {
      if (shouldSkipPath(relPath)) continue;
      const fullPath = path.join(ROOT, relPath);
      if (!fs.existsSync(fullPath)) continue;

      scannedFiles++;
      const normPath = relPath.replace(/\\/g, '/');
      try {
        const content = fs.readFileSync(fullPath, 'utf-8');
        const rawLines = content.split(/\r?\n/);
        for (let i = 0; i < rawLines.length; i++) {
          checkLine(normPath, i + 1, rawLines[i]);
        }
      } catch (err) {
        continue;
      }
    }
  }

  const elapsed = Date.now() - startTime;

  if (violationCount > 0) {
    console.error(`\n${colors.red}${colors.bold}🚫 安全门禁未通过！发现 ${violationCount} 处敏感凭据风险（耗时 ${elapsed}ms，扫描 ${scannedFiles} 个变更文件）。${colors.reset}`);
    console.error(`${colors.yellow}👉 请参考 AGENT.md 第 7 节使用合规占位符，或将真实凭据存入 /env/.envdev。${colors.reset}\n`);
    process.exit(1);
  }

  console.log(`${colors.green}✅ [Security Gate] 验证通过！未发现敏感凭据泄露风险（扫描 ${scannedFiles} 个文件，耗时 ${elapsed}ms）。${colors.reset}`);
  process.exit(0);
}

runSecurityGate();
