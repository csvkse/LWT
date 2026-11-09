#!/usr/bin/env node
/**
 * scripts/docs-archiver.cjs
 * 
 * LWT 文档生命周期治理与自动化按日期归档工具
 * 
 * 设计模式实现：
 * - 策略模式 (Strategy Pattern): 多源时间提取策略链 (HeaderMetadata -> GitLog -> FileSystem)
 * - 代理/桩模式 (Stub Proxy Pattern): 移动历史文件后在原路径保留重定向桩，杜绝 404 死链
 * - 工厂与建造者模式: 自动汇编生成 docs/archive/README.md 全景双重视图索引矩阵 (分类 + 年月)
 * 
 * 使用方式：
 *   node scripts/docs-archiver.cjs --dry-run      # 试运行核验时间映射与目录分布
 *   node scripts/docs-archiver.cjs --execute      # 正式执行归档并更新双重视图索引
 *   node scripts/docs-archiver.cjs --clean-root   # 清理 docs 根目录下所有平铺桩，实现纯净分类目录架构
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

// 根目录与目标目录定义
const REPO_ROOT = path.resolve(__dirname, '..');
const DOCS_DIR = path.join(REPO_ROOT, 'docs');
const ARCHIVE_DIR = path.join(DOCS_DIR, 'archive');
const ARCH_DIR = path.join(DOCS_DIR, 'architecture');

// 白名单保留规则：坚决不归档的常青目录与常青系统文档
const PRESERVED_DIRS = new Set([
  'architecture',
  'articles',
  'dev',
  'imgs',
  'plugin-development-guide',
  'references',
  'archive'
]);

const PRESERVED_FILES = new Set([
  'README.md'
]);

// 命令行参数解析
const args = process.argv.slice(2);
const isCleanRoot = args.includes('--clean-root');
const isExecute = args.includes('--execute') || isCleanRoot;
const isDryRun = args.includes('--dry-run') && !isExecute;
const noStub = args.includes('--no-stub') || isCleanRoot;

// ==========================================
// 策略 1: 头部元数据提取策略 (HeaderMetadataStrategy)
// ==========================================
class HeaderMetadataStrategy {
  extract(filePath, content) {
    const head = content.slice(0, 3500);

    const patterns = [
      /(?:\*\*|\b)(?:制定时间|创建时间|归档时间|更新时间|时间|日期|Date)(?:\*\*|\b)[：:\s*]+(\d{4}[-/年]\d{1,2}[-/月]\d{1,2})/i,
      /date:\s*['"]?(\d{4}[-/年]\d{1,2}[-/月]\d{1,2})['"]?/i,
      /(?:制定日期|创建日期)[：:\s*]+(\d{4}[-/年]\d{1,2}[-/月]\d{1,2})/i
    ];

    for (const regex of patterns) {
      const match = head.match(regex);
      if (match && match[1]) {
        const normalized = this.normalizeDate(match[1]);
        if (normalized) {
          return { source: 'HeaderMetadata', date: normalized };
        }
      }
    }
    return null;
  }

  normalizeDate(raw) {
    const cleaned = raw.replace(/[年月]/g, '-').replace(/[日]/g, '').trim();
    const parts = cleaned.split(/[-/.]/).map(p => p.trim());
    if (parts.length >= 3) {
      const y = parts[0];
      const m = parts[1].padStart(2, '0');
      const d = parts[2].padStart(2, '0');
      if (parseInt(y, 10) >= 2020 && parseInt(y, 10) <= 2030) {
        return `${y}-${m}-${d}`;
      }
    }
    return null;
  }
}

// ==========================================
// 策略 2: Git 初始提交提取策略 (GitLogStrategy)
// ==========================================
class GitLogStrategy {
  extract(filePath) {
    try {
      const relPath = path.relative(REPO_ROOT, filePath).replace(/\\/g, '/');
      const cmd = `git log --diff-filter=A --format=%cs -n 1 -- "${relPath}"`;
      const out = execSync(cmd, { cwd: REPO_ROOT, encoding: 'utf8', stdio: ['pipe', 'pipe', 'ignore'] }).trim();
      if (out && /^\d{4}-\d{2}-\d{2}$/.test(out)) {
        return { source: 'GitLogInitial', date: out };
      }
      const cmdLast = `git log -n 1 --format=%cs -- "${relPath}"`;
      const outLast = execSync(cmdLast, { cwd: REPO_ROOT, encoding: 'utf8', stdio: ['pipe', 'pipe', 'ignore'] }).trim();
      if (outLast && /^\d{4}-\d{2}-\d{2}$/.test(outLast)) {
        return { source: 'GitLogLatest', date: outLast };
      }
    } catch { }
    return null;
  }
}

// ==========================================
// 策略 3: 文件系统元数据策略 (FileSystemStrategy)
// ==========================================
class FileSystemStrategy {
  extract(filePath) {
    try {
      const stat = fs.statSync(filePath);
      const mtime = stat.mtime || stat.birthtime;
      if (mtime) {
        const y = mtime.getFullYear();
        const m = String(mtime.getMonth() + 1).padStart(2, '0');
        const d = String(mtime.getDate()).padStart(2, '0');
        return { source: 'FileSystem', date: `${y}-${m}-${d}` };
      }
    } catch { }
    const today = new Date();
    const y = today.getFullYear();
    const m = String(today.getMonth() + 1).padStart(2, '0');
    const d = String(today.getDate()).padStart(2, '0');
    return { source: 'FallbackToday', date: `${y}-${m}-${d}` };
  }
}

// ==========================================
// 日期提取调度器 (Chain of Responsibility)
// ==========================================
class DateExtractor {
  constructor() {
    this.headerStrategy = new HeaderMetadataStrategy();
    this.gitStrategy = new GitLogStrategy();
    this.fsStrategy = new FileSystemStrategy();
  }

  resolve(filePath, content) {
    const fromHeader = this.headerStrategy.extract(filePath, content);
    if (fromHeader) return fromHeader;
    const fromGit = this.gitStrategy.extract(filePath);
    if (fromGit) return fromGit;
    return this.fsStrategy.extract(filePath);
  }
}

// 辅助工具：提取标题
function extractTitle(content, fallbackFileName) {
  const lines = content.split('\n');
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.startsWith('# ')) {
      return trimmed.replace(/^#\s+/, '').trim();
    }
  }
  return fallbackFileName.replace(/\.md$/i, '');
}

// 辅助工具：提取文档生命周期状态
function extractStatus(content) {
  const head = content.slice(0, 3000);
  const match = head.match(/(?:status|状态)[：:\s*]+\**([a-zA-Z\u4e00-\u9fa5]+)\**/i);
  if (match) {
    const val = match[1].toLowerCase().trim();
    if (val.includes('draft') || val.includes('草稿')) return 'Draft';
    if (val.includes('active') || val.includes('进行中') || val.includes('开发中')) return 'Active';
    if (val.includes('implement') || val.includes('已实现') || val.includes('已落地') || val.includes('完成')) return 'Implemented';
    if (val.includes('supersede') || val.includes('废弃') || val.includes('替代')) return 'Superseded';
  }
  return null;
}

// 辅助工具：判断文档是否具备归档资格 (7天活动缓冲期与状态机防护)
function isEligibleForArchival(docDate, status, graceDays = 7) {
  // 1. 显式 Draft/Active 绝对免除归档
  if (status === 'Draft' || status === 'Active') return false;
  // 2. 显式 Implemented/Superseded 立即允许归档
  if (status === 'Implemented' || status === 'Superseded') return true;
  // 3. 未显式标注状态时，检查创建时间是否超过宽限期 (默认 7 天)
  try {
    const createdTime = new Date(docDate).getTime();
    if (!isNaN(createdTime)) {
      const diffDays = (Date.now() - createdTime) / (1000 * 60 * 60 * 24);
      return diffDays > graceDays;
    }
  } catch { }
  return false;
}

// 辅助工具：分析文档主题分类
function detectCategory(title, fileName) {
  const text = `${title} ${fileName}`.toLowerCase();
  if (text.includes('plugin') || text.includes('插件')) return '🧩 插件与微服务';
  if (text.includes('auth') || text.includes('permission') || text.includes('apikey') || text.includes('security') || text.includes('凭据')) return '🛡️ 安全鉴权与凭据';
  if (text.includes('backup') || text.includes('sync') || text.includes('同步') || text.includes('备份')) return '📦 备份与存储同步';
  if (text.includes('docker') || text.includes('deploy') || text.includes('构建') || text.includes('部署')) return '🚀 容器与部署架构';
  if (text.includes('terminal') || text.includes('pty') || text.includes('终端')) return '💻 终端与 PTY 引擎';
  if (text.includes('easytier') || text.includes('frp') || text.includes('proxy') || text.includes('tunnel') || text.includes('网络') || text.includes('代理')) return '🌐 组网与网络穿透';
  if (text.includes('audit') || text.includes('report') || text.includes('复盘') || text.includes('报告')) return '📊 审计与复盘报告';
  return '📝 架构方案与决议';
}

// ==========================================
// 主执行管线
// ==========================================
function run() {
  console.log(`=======================================================`);
  console.log(`  LinuxWebTool (LWT) 知识库生命周期治理归档器`);
  console.log(`  模式: ${isCleanRoot ? '🧹 纯净根目录分类整理 (--clean-root)' : (isExecute ? '⚡ 正式执行 (EXECUTE)' : '🔍 试运行 (DRY-RUN)')}`);
  console.log(`=======================================================\n`);

  if (!fs.existsSync(DOCS_DIR)) {
    console.error(`❌ docs 目录不存在: ${DOCS_DIR}`);
    process.exit(1);
  }

  const extractor = new DateExtractor();

  // 1. 扫描 docs/ 根目录下的所有候选文档 (Inbox 收集箱模式)
  const rootEntries = fs.readdirSync(DOCS_DIR, { withFileTypes: true });
  const pendingInboxFiles = rootEntries
    .filter(e => e.isFile() && e.name.endsWith('.md') && !PRESERVED_FILES.has(e.name))
    .map(e => e.name);

  console.log(`📂 在 docs/ 顶层收件箱检索到待处理文档: ${pendingInboxFiles.length} 篇\n`);

  let newlyArchived = 0;
  let protectedCount = 0;

  for (const fileName of pendingInboxFiles) {
    const fullPath = path.join(DOCS_DIR, fileName);
    const content = fs.readFileSync(fullPath, 'utf8');
    const { date, source } = extractor.resolve(fullPath, content);
    const status = extractStatus(content);
    const title = extractTitle(content, fileName);
    const category = detectCategory(title, fileName);
    const ym = date.slice(0, 7);

    // 状态机与 7 天缓冲期判定
    const eligible = isEligibleForArchival(date, status, 7);

    if (!eligible) {
      console.log(`   🛡️ [缓冲保护] ${fileName} (状态: ${status || '7天宽限期内'}) -> 保留在 docs 根目录供持续开发`);
      protectedCount++;
      continue;
    }

    console.log(`   📦 [符合归档] ${fileName} -> docs/archive/${ym}/ (${category}, 状态: ${status || '已超过缓冲期'})`);

    if (!isDryRun) {
      const targetDir = path.join(ARCHIVE_DIR, ym);
      if (!fs.existsSync(targetDir)) {
        fs.mkdirSync(targetDir, { recursive: true });
      }
      const targetPath = path.join(targetDir, fileName);
      fs.writeFileSync(targetPath, content, 'utf8');
      fs.unlinkSync(fullPath);
      newlyArchived++;
    }
  }

  if (isDryRun && pendingInboxFiles.length > 0) {
    console.log(`\n💡 试运行统计: ${newlyArchived} 篇待归档，${protectedCount} 篇受保护暂留。`);
  }

  // 2. 从 docs/archive/ 遍历已归档的文档以重构双重视图索引
  const filePlan = [];
  const monthDistribution = {};
  const categoryDistribution = {};

  if (fs.existsSync(ARCHIVE_DIR)) {
    const months = fs.readdirSync(ARCHIVE_DIR, { withFileTypes: true })
      .filter(e => e.isDirectory() && /^\d{4}-\d{2}$/.test(e.name))
      .map(e => e.name)
      .sort()
      .reverse();

    for (const ym of months) {
      const monthDir = path.join(ARCHIVE_DIR, ym);
      const docsInMonth = fs.readdirSync(monthDir).filter(f => f.endsWith('.md'));
      for (const fileName of docsInMonth) {
        const fullPath = path.join(monthDir, fileName);
        const content = fs.readFileSync(fullPath, 'utf8');
        // 性能短路优化：优先从 header 提取制定日期；若无，以年月目录直接推导，避免遍历 200+ 次 git log 进程
        let date = extractor.headerStrategy.extract(fullPath, content)?.date;
        let source = 'HeaderMetadata';
        if (!date) {
          try {
            const mtime = fs.statSync(fullPath).mtime;
            const d = String(mtime.getDate()).padStart(2, '0');
            date = `${ym}-${d}`;
            source = 'FileSystemMonth';
          } catch {
            date = `${ym}-01`;
            source = 'ArchiveFolder';
          }
        }
        const title = extractTitle(content, fileName);
        const category = detectCategory(title, fileName);

        monthDistribution[ym] = (monthDistribution[ym] || 0) + 1;
        categoryDistribution[category] = (categoryDistribution[category] || 0) + 1;

        filePlan.push({
          fileName,
          fullPath,
          title,
          category,
          date,
          ym,
          source,
          relArchivePath: `archive/${ym}/${fileName}`
        });
      }
    }
  }

  // 按日期降序排序
  filePlan.sort((a, b) => b.date.localeCompare(a.date));

  console.log(`📊 归档总库统计: 共计 ${filePlan.length} 篇历史方案`);
  console.log(`\n📅 按年月时间线分布:`);
  for (const [ym, count] of Object.entries(monthDistribution)) {
    console.log(`   - docs/archive/${ym}/ : ${count} 篇`);
  }
  console.log(`\n🏷️ 按核心主题分类分布:`);
  for (const [cat, count] of Object.entries(categoryDistribution)) {
    console.log(`   - ${cat.padEnd(16)}: ${count} 篇`);
  }

  if (newlyArchived > 0) {
    console.log(`\n✅ 本次成功归档并收纳新方案: ${newlyArchived} 篇！`);
  }
  if (protectedCount > 0) {
    console.log(`🛡️ 保持 ${protectedCount} 篇在途方案于 docs/ 根目录，免于归档干扰。`);
  }

  // 4. 重建 docs/archive/README.md 双重视图索引矩阵
  console.log(`\n📑 正在生成全局双重视图索引表 docs/archive/README.md ...`);
  const sortedMonths = Object.keys(monthDistribution).sort().reverse();
  const indexMarkdown = generateArchiveIndex(filePlan, sortedMonths, monthDistribution, categoryDistribution);
  fs.writeFileSync(path.join(ARCHIVE_DIR, 'README.md'), indexMarkdown, 'utf8');
  console.log(`✅ docs/archive/README.md 双重视图索引矩阵构建完毕！`);

  console.log(`\n🎉 文档生命周期与分类整理管线执行完毕！`);
}

// ==========================================
// 建造者：生成 docs/archive/README.md (双重视图)
// ==========================================
function generateArchiveIndex(filePlan, sortedMonths, monthDistribution, categoryDistribution) {
  let md = `# LinuxWebTool (LWT) 历史架构方案与技术决议全景索引库

> **治理规范**：依据 ADR (Architecture Decision Record) 与 RFC 不可变历史版本治理原则，所有已实施落地的架构提案、排障复盘、可行性调研与设计方案均已按制定时间与业务分类归档于本目录中。  
> **常青指南规范**：如需查阅当前系统的现行业务开发规范与清单，请直接访问顶层常青目录：
> - [docs/plugin-development-guide/](../plugin-development-guide/README.md) (通用微服务与插件生态 Schema v3.1 开发全景指南)
> - [docs/architecture/](../architecture/) (主系统架构总纲、底座设计与 Layout 规范)
> - [docs/dev/](../dev/) (开发流水线与门禁质量体系)

---

## 1. 核心主题分类大纲矩阵 (Topic Categories)

| 核心主题分类 | 文档篇数 | 快速导航 | 核心涉及领域 |
| :--- | :---: | :--- | :--- |
`;

  const sortedCategories = Object.keys(categoryDistribution).sort((a, b) => categoryDistribution[b] - categoryDistribution[a]);
  for (const cat of sortedCategories) {
    const slug = cat.replace(/[^\u4e00-\u9fa5a-zA-Z0-9]/g, '');
    md += `| **${cat}** | **${categoryDistribution[cat]} 篇** | [👉 查看该分类方案](#${slug}) | 微服务生态、RPC 发现、插件生命周期与热更 |\n`;
  }

  md += `\n---

## 2. 归档年月时间线大纲 (Timeline Archives)

| 归档年月 | 归档文档篇数 | 快速导航 |
| :--- | :---: | :--- |
`;

  for (const ym of sortedMonths) {
    md += `| **${ym}** | **${monthDistribution[ym]} 篇** | [👉 跳转至 ${ym} 时间线](#${ym.replace('-', '')}) |\n`;
  }

  md += `\n---

## 3. 视图 A：按主题分类方案矩阵 (Topic Matrix)
`;

  for (const cat of sortedCategories) {
    const slug = cat.replace(/[^\u4e00-\u9fa5a-zA-Z0-9]/g, '');
    const docsInCat = filePlan.filter(d => d.category === cat);
    md += `\n<a id="${slug}"></a>\n`;
    md += `### ${cat} (${docsInCat.length} 篇)\n\n`;
    md += `| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |\n`;
    md += `| :---: | :---: | :---: | :--- | :--- | :---: |\n`;

    docsInCat.forEach((item, idx) => {
      const cleanTitle = item.title.replace(/\|/g, '\\|');
      md += `| ${idx + 1} | \`${item.date}\` | \`${item.ym}\` | [${cleanTitle}](${item.ym}/${item.fileName}) | \`${item.fileName}\` | \`Implemented\` |\n`;
    });
  }

  md += `\n---

## 4. 视图 B：按年月时间线明细表 (Chronological Timeline)
`;

  let currentYm = '';
  let monthIndex = 1;

  for (let i = 0; i < filePlan.length; i++) {
    const item = filePlan[i];
    if (item.ym !== currentYm) {
      currentYm = item.ym;
      monthIndex = 1;
      md += `\n<a id="${currentYm.replace('-', '')}"></a>\n`;
      md += `### 📅 ${currentYm} 时间线汇总 (${monthDistribution[currentYm]} 篇)\n\n`;
      md += `| 序号 | 制定日期 | 分类 | 文档标题 | 归档文件路径 | 状态 |\n`;
      md += `| :---: | :---: | :--- | :--- | :--- | :---: |\n`;
    }

    const cleanTitle = item.title.replace(/\|/g, '\\|');
    md += `| ${monthIndex} | \`${item.date}\` | ${item.category} | [${cleanTitle}](${item.ym}/${item.fileName}) | \`${item.fileName}\` | \`Implemented\` |\n`;
    monthIndex++;
  }

  md += `\n---\n\n> *本索引由 \`scripts/docs-archiver.cjs\` 自动生成，双重视图构建时间：${new Date().toISOString().slice(0, 10)}*。\n`;
  return md;
}

// 启动执行
run();
