/**
 * LinuxWebTool 磁盘空间透视镜前端应用状态机与可视化引擎 (Disk Space Explorer ESM)
 */

import { LwtNavigationBridge } from './navigationBridge.js';

const token = new URLSearchParams(location.search).get('token') || '';
const headers = token ? { Authorization: `Bearer ${token}` } : {};

// DOM 元素引用
const envBadge = document.getElementById('env-badge');
const btnRefreshEnv = document.getElementById('btn-refresh-env');
const targetSwitcher = document.getElementById('target-switcher');
const targetSummary = document.getElementById('target-summary');
const inputPath = document.getElementById('input-path');
const quickPathsList = document.getElementById('quick-paths-list');
const btnStartScan = document.getElementById('btn-start-scan');
const btnAbortScan = document.getElementById('btn-abort-scan');
const btnToggleOptions = document.getElementById('btn-toggle-options');
const advancedPanel = document.getElementById('advanced-options-panel');
const toggleArrow = document.getElementById('toggle-arrow');
const chkOneFs = document.getElementById('chk-one-fs');
const selectDepth = document.getElementById('select-depth');
const selectEngine = document.getElementById('select-engine');

const progressCard = document.getElementById('progress-card');
const progressStatusText = document.getElementById('progress-status-text');
const metricItems = document.getElementById('metric-items');
const metricElapsed = document.getElementById('metric-elapsed');
const metricSize = document.getElementById('metric-size');
const progressCurrentPathText = document.getElementById('progress-current-path-text');

const alertBox = document.getElementById('alert-box');
const alertMessage = document.getElementById('alert-message');
const btnCloseAlert = document.getElementById('btn-close-alert');

const resultCard = document.getElementById('result-card');
const breadcrumbs = document.getElementById('breadcrumbs');
const resultTotalBadge = document.getElementById('result-total-badge');
const btnViewTree = document.getElementById('btn-view-tree');
const btnViewTreemap = document.getElementById('btn-view-treemap');
const viewTreeContainer = document.getElementById('view-tree-container');
const viewTreemapContainer = document.getElementById('view-treemap-container');
const treeListBody = document.getElementById('tree-list-body');
const treemapCanvas = document.getElementById('treemap-canvas');
const treemapTooltip = document.getElementById('treemap-tooltip');

// 抽屉 DOM 元素引用
const unrankedDrawerBackdrop = document.getElementById('unranked-drawer-backdrop');
const unrankedDrawer = document.getElementById('unranked-drawer');
const drawerSubtitle = document.getElementById('drawer-subtitle');
const btnCloseDrawer = document.getElementById('btn-close-drawer');
const inputUnrankedSearch = document.getElementById('input-unranked-search');
const drawerStats = document.getElementById('drawer-stats');
const drawerListBody = document.getElementById('drawer-list-body');
const drawerFooter = document.getElementById('drawer-footer');
const btnUnrankedMore = document.getElementById('btn-unranked-more');

// 全局内存状态
let capabilities = null;
let currentTarget = 'container';
let currentTaskId = null;
let activeEventSource = null;
let fullResultRoot = null;
let navigationStack = [];
let currentView = 'tree';

// 抽屉分页与筛选状态
let currentUnrankedParentPath = '';
let currentUnrankedPage = 1;
let currentUnrankedKeyword = '';
let unrankedDebounceTimer = null;

/**
 * 封装通用 API 请求
 */
async function apiFetch(endpoint, options = {}) {
  const cleanEndpoint = endpoint.replace(/^\/+/, '');
  const url = `/plugins/disk-explorer/${cleanEndpoint}`;
  const sep = url.includes('?') ? '&' : '?';
  const finalUrl = token ? `${url}${sep}token=${encodeURIComponent(token)}` : url;

  const res = await fetch(finalUrl, {
    ...options,
    headers: {
      ...headers,
      ...(options.headers || {}),
    },
  });

  if (!res.ok) {
    let errMsg = `HTTP ${res.status}`;
    try {
      const errJson = await res.json();
      if (errJson && errJson.error) errMsg = errJson.error;
    } catch (_) {}
    throw new Error(errMsg);
  }

  return await res.json();
}

/**
 * 初始化自省环境能力
 */
async function initCapabilities() {
  try {
    envBadge.textContent = '检测中...';
    envBadge.className = 'badge badge-warning';

    const resp = await apiFetch('api/capabilities');
    if (!resp.success || !resp.data) {
      throw new Error('获取环境能力响应异常');
    }

    capabilities = resp.data;

    // 1. 设置顶部环境能力徽标
    if (capabilities.isContainer) {
      if (capabilities.canAccessHost) {
        envBadge.textContent = `Docker 特权容器 (支持宿主穿透: ${capabilities.hostDistro || 'Linux'})`;
        envBadge.className = 'badge badge-success';
      } else {
        envBadge.textContent = 'Docker 沙箱容器 (无宿主穿透)';
        envBadge.className = 'badge badge-info';
      }
    } else {
      envBadge.textContent = `原生操作系统 (${capabilities.hostDistro || capabilities.platformOs})`;
      envBadge.className = 'badge badge-success';
    }

    // 2. 动态渲染目标选择器
    renderTargetSwitcher();
  } catch (err) {
    showError(`环境能力探测失败: ${err.message}`);
    envBadge.textContent = '探测受限';
    envBadge.className = 'badge badge-danger';
  }
}

/**
 * 渲染目标命名空间选择器（特权识别严格防御）
 */
function renderTargetSwitcher() {
  if (!capabilities || !Array.isArray(capabilities.availableTargets)) return;

  targetSwitcher.innerHTML = '';
  capabilities.availableTargets.forEach((target, index) => {
    const item = document.createElement('div');
    item.className = `target-item ${target.id === currentTarget ? 'active' : ''}`;
    item.dataset.target = target.id;

    const icon = target.id === 'host' ? '🖥️' : '📦';
    item.innerHTML = `
      <span class="icon">${icon}</span>
      <div class="info">
        <span class="name">${escapeHtml(target.name)}</span>
        <span class="desc">${escapeHtml(target.description)}</span>
      </div>
    `;

    item.addEventListener('click', () => {
      switchTarget(target.id);
    });

    targetSwitcher.appendChild(item);
  });

  // 如果当前选中的目标在可用列表中不存在，回退到第一个
  if (!capabilities.availableTargets.some(t => t.id === currentTarget)) {
    currentTarget = capabilities.availableTargets[0].id;
  }

  updateTargetUI();
}

/**
 * 切换目标命名空间
 */
function switchTarget(targetId) {
  currentTarget = targetId;
  const items = targetSwitcher.querySelectorAll('.target-item');
  items.forEach(el => {
    el.classList.toggle('active', el.dataset.target === targetId);
  });
  updateTargetUI();
}

/**
 * 更新目标相关的 UI 提示与快捷路径
 */
function updateTargetUI() {
  const currentTargetObj = capabilities?.availableTargets?.find(t => t.id === currentTarget);
  if (!currentTargetObj) return;

  targetSummary.textContent = currentTargetObj.description;
  inputPath.value = currentTargetObj.defaultPath || '/';

  // 渲染快捷药丸
  quickPathsList.innerHTML = '';
  (currentTargetObj.quickPaths || []).forEach(p => {
    const pill = document.createElement('button');
    pill.type = 'button';
    pill.className = 'pill-btn';
    pill.textContent = p;
    pill.addEventListener('click', () => {
      inputPath.value = p;
      startScan();
    });
    quickPathsList.appendChild(pill);
  });
}

/**
 * 发起扫描任务
 */
async function startScan() {
  hideError();
  const path = inputPath.value.trim();
  if (!path) {
    showError('请输入需要分析的起始路径');
    return;
  }

  if (activeEventSource) {
    activeEventSource.close();
    activeEventSource = null;
  }

  const payload = {
    target: currentTarget,
    path: path,
    oneFileSystem: chkOneFs.checked,
    maxDepth: parseInt(selectDepth.value, 10) || 3,
    engine: selectEngine.value,
  };

  btnStartScan.classList.add('hidden');
  btnAbortScan.classList.remove('hidden');
  progressCard.classList.remove('hidden');
  resultCard.classList.add('hidden');

  progressStatusText.textContent = '正在初始化分析任务...';
  metricItems.textContent = '0';
  metricElapsed.textContent = '0 ms';
  metricSize.textContent = '0 B';
  progressCurrentPathText.textContent = path;

  try {
    const res = await apiFetch('api/scans', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    if (!res.success || !res.data) {
      throw new Error('任务启动失败');
    }

    currentTaskId = res.data.taskId;
    subscribeProgress(currentTaskId);
  } catch (err) {
    showError(`启动扫描失败: ${err.message}`);
    resetScanButtons();
    progressCard.classList.add('hidden');
  }
}

/**
 * 订阅 SSE 实时进度事件 (配合毫秒级轮询兜底)
 */
function subscribeProgress(taskId) {
  let isDone = false;
  const sseUrl = `/plugins/disk-explorer/api/scans/${encodeURIComponent(taskId)}/progress${token ? '?token=' + encodeURIComponent(token) : ''}`;
  activeEventSource = new EventSource(sseUrl);

  const handleUpdate = (p) => {
    if (isDone || !p) return;
    updateProgressDisplay(p);

    if (p.status === 'Completed') {
      isDone = true;
      if (activeEventSource) {
        activeEventSource.close();
        activeEventSource = null;
      }
      finishScanSuccess(taskId);
    } else if (p.status === 'Cancelled') {
      isDone = true;
      if (activeEventSource) {
        activeEventSource.close();
        activeEventSource = null;
      }
      finishScanAborted();
    } else if (p.status === 'Failed' || p.status === 'Timeout') {
      isDone = true;
      if (activeEventSource) {
        activeEventSource.close();
        activeEventSource = null;
      }
      finishScanFailed(p.errorMessage || '分析异常终止');
    }
  };

  activeEventSource.addEventListener('progress', (e) => {
    try {
      const p = JSON.parse(e.data);
      handleUpdate(p);
    } catch (err) {
      console.warn('进度事件解析警告:', err);
    }
  });

  // 辅助轮询兜底，防御中间件代理对 SSE 流的 chunk 缓冲
  const pollTimer = setInterval(async () => {
    if (isDone) {
      clearInterval(pollTimer);
      return;
    }
    try {
      const res = await apiFetch(`api/scans/${encodeURIComponent(taskId)}/status`);
      if (res.success && res.data && res.data.progress) {
        handleUpdate(res.data.progress);
      }
    } catch (_) {}
  }, 400);

  activeEventSource.onerror = () => {
    // 保持轮询持续同步
  };
}

/**
 * 更新实时扫描进度看板
 */
function updateProgressDisplay(p) {
  if (p.status === 'Scanning') {
    progressStatusText.textContent = '正在遍历目录树...';
  } else if (p.status === 'Aggregating') {
    progressStatusText.textContent = '正在聚合空间占比与剪枝...';
  } else if (p.status === 'Completed') {
    progressStatusText.textContent = '分析完成！';
  }

  metricItems.textContent = (p.scannedFiles + p.scannedDirs).toLocaleString();
  metricElapsed.textContent = `${p.elapsedMs || 0} ms`;
  if (p.totalBytes) {
    metricSize.textContent = formatBytes(p.totalBytes);
  }
  if (p.currentPath) {
    progressCurrentPathText.textContent = p.currentPath;
  }
}

/**
 * 单次轮询兜底检查状态
 */
async function pollStatusFallback(taskId) {
  try {
    const res = await apiFetch(`api/scans/${encodeURIComponent(taskId)}/status`);
    if (res.success && res.data) {
      const snap = res.data;
      if (snap.status === 'Completed') {
        finishScanSuccess(taskId);
      } else if (snap.status === 'Failed') {
        finishScanFailed(snap.progress?.errorMessage || '扫描失败');
      }
    }
  } catch (_) {}
}

/**
 * 扫描成功处理
 */
async function finishScanSuccess(taskId) {
  if (activeEventSource) {
    activeEventSource.close();
    activeEventSource = null;
  }
  resetScanButtons();
  progressCard.classList.add('hidden');

  try {
    const res = await apiFetch(`api/scans/${encodeURIComponent(taskId)}/tree`);
    if (!res.success || !res.data || !res.data.root) {
      throw new Error('未获取到有效的结果目录树');
    }

    fullResultRoot = res.data.root;
    navigationStack = [fullResultRoot];

    resultCard.classList.remove('hidden');
    resultTotalBadge.textContent = `总量: ${fullResultRoot.humanSize || formatBytes(fullResultRoot.sizeBytes)}`;

    renderCurrentLevel();
  } catch (err) {
    showError(`读取分析结果失败: ${err.message}`);
  }
}

/**
 * 扫描被主动终止处理
 */
function finishScanAborted() {
  if (activeEventSource) {
    activeEventSource.close();
    activeEventSource = null;
  }
  resetScanButtons();
  progressCard.classList.add('hidden');
  showError('扫描分析已被用户主动终止。');
}

/**
 * 扫描失败处理
 */
function finishScanFailed(errMsg) {
  if (activeEventSource) {
    activeEventSource.close();
    activeEventSource = null;
  }
  resetScanButtons();
  progressCard.classList.add('hidden');
  showError(`扫描分析失败: ${errMsg}`);
}

/**
 * 主动终止扫描
 */
async function abortScan() {
  if (!currentTaskId) return;
  try {
    btnAbortScan.disabled = true;
    btnAbortScan.textContent = '终止中...';
    await apiFetch(`api/scans/${encodeURIComponent(currentTaskId)}/abort`, { method: 'POST' });
  } catch (err) {
    console.warn('中止扫描请求失败:', err);
  } finally {
    btnAbortScan.disabled = false;
    btnAbortScan.textContent = '🛑 终止分析';
  }
}

function resetScanButtons() {
  btnStartScan.classList.remove('hidden');
  btnAbortScan.classList.add('hidden');
}

/**
 * 渲染当前层级的数据（树状排行榜与矩形树图）
 */
function renderCurrentLevel() {
  const currentNode = navigationStack[navigationStack.length - 1];
  if (!currentNode) return;

  renderBreadcrumbs();

  if (currentView === 'tree') {
    renderTree(currentNode);
  } else {
    renderTreemap(currentNode);
  }
}

/**
 * 渲染面包屑导航
 */
function renderBreadcrumbs() {
  breadcrumbs.innerHTML = '';
  navigationStack.forEach((node, idx) => {
    const isLast = idx === navigationStack.length - 1;

    const span = document.createElement('span');
    span.className = 'breadcrumb-item';
    span.textContent = node.name || node.path || '/';
    if (!isLast) {
      span.addEventListener('click', () => {
        navigationStack = navigationStack.slice(0, idx + 1);
        renderCurrentLevel();
      });
    } else {
      span.style.color = '#fff';
      span.style.fontWeight = 'bold';
    }
    breadcrumbs.appendChild(span);

    if (!isLast) {
      const sep = document.createElement('span');
      sep.className = 'breadcrumb-sep';
      sep.textContent = '/';
      breadcrumbs.appendChild(sep);
    }
  });
}

/**
 * 打开微小内容明细抽屉
 */
function openUnrankedDrawer(parentPath) {
  currentUnrankedParentPath = parentPath;
  currentUnrankedPage = 1;
  currentUnrankedKeyword = '';
  inputUnrankedSearch.value = '';
  drawerSubtitle.textContent = parentPath;
  unrankedDrawerBackdrop.classList.remove('hidden');
  unrankedDrawer.classList.remove('hidden');
  fetchUnrankedItems(true);
}

/**
 * 关闭微小内容明细抽屉
 */
function closeUnrankedDrawer() {
  unrankedDrawerBackdrop.classList.add('hidden');
  unrankedDrawer.classList.add('hidden');
}

/**
 * 分页拉取微小项明细
 */
async function fetchUnrankedItems(reset = false) {
  if (!currentTaskId || !currentUnrankedParentPath) return;
  if (reset) {
    currentUnrankedPage = 1;
    drawerListBody.innerHTML = '<div style="padding: 20px; text-align: center; color: var(--text-muted); font-size: 0.85rem;"><span class="spinner-inline"></span> 正在拉取微小项明细...</div>';
  }

  try {
    const params = new URLSearchParams({
      path: currentUnrankedParentPath,
      page: String(currentUnrankedPage),
      pageSize: '50',
    });
    if (currentUnrankedKeyword) {
      params.set('keyword', currentUnrankedKeyword);
    }

    const res = await apiFetch(`api/scans/${encodeURIComponent(currentTaskId)}/unranked?${params.toString()}`);
    if (!res.success || !res.data) {
      throw new Error('获取明细响应异常');
    }

    const { items, hasMore } = res.data;
    const count = res.data.totalUnranked ?? res.data.totalCount ?? (items ? items.length : 0);
    drawerStats.textContent = `共 ${count} 项 (${res.data.humanSize || ''})`;

    if (reset) {
      drawerListBody.innerHTML = '';
    }

    if (!items || items.length === 0) {
      if (reset) {
        drawerListBody.innerHTML = '<div style="padding: 24px; text-align: center; color: var(--text-muted); font-size: 0.85rem;">无符合条件的条目</div>';
      }
      drawerFooter.classList.add('hidden');
      return;
    }

    items.forEach(item => {
      const row = document.createElement('div');
      row.className = 'drawer-row';
      const icon = item.isDir ? '📁' : '📄';

      row.innerHTML = `
        <div class="col-name" title="${escapeHtml(item.path)}">
          <span class="node-icon">${icon}</span>
          <span class="node-name" style="font-size: 0.82rem;">${escapeHtml(item.name || item.path)}</span>
        </div>
        <div class="col-size" style="font-size: 0.82rem;">${escapeHtml(item.humanSize || formatBytes(item.sizeBytes))}</div>
        <div class="col-actions">
          <button class="btn-xs btn-nav-files" title="在系统文件管理器中查看">📁</button>
          ${item.isDir ? `
            <button class="btn-xs btn-nav-term" title="在终端中打开">💻</button>
            <button class="btn-xs btn-drill-drawer" title="下钻进入此目录">➔</button>
          ` : ''}
        </div>
      `;

      row.querySelector('.btn-nav-files').addEventListener('click', (e) => {
        e.stopPropagation();
        LwtNavigationBridge.navigateToFile({
          path: item.path,
          isDir: Boolean(item.isDir),
          target: currentTarget,
        });
      });

      if (item.isDir) {
        row.querySelector('.btn-nav-term').addEventListener('click', (e) => {
          e.stopPropagation();
          LwtNavigationBridge.navigateToTerminal({
            cwd: item.path,
            isDir: true,
            target: currentTarget,
          });
        });

        row.querySelector('.btn-drill-drawer').addEventListener('click', async (e) => {
          e.stopPropagation();
          closeUnrankedDrawer();
          await drillIntoPath(item);
        });
      }

      drawerListBody.appendChild(row);
    });

    if (hasMore) {
      drawerFooter.classList.remove('hidden');
    } else {
      drawerFooter.classList.add('hidden');
    }
  } catch (err) {
    if (reset) {
      drawerListBody.innerHTML = `<div style="padding: 16px; text-align: center; color: var(--accent-red); font-size: 0.82rem;">拉取失败: ${escapeHtml(err.message)}</div>`;
    }
  }
}

/**
 * 递归或即时探测下钻子目录 (JIT On-Demand Probe & Grafting)
 */
async function drillIntoPath(child, triggerBtn = null) {
  if (child.children && child.children.length > 0) {
    navigationStack.push(child);
    renderCurrentLevel();
    return;
  }

  let origText = '';
  if (triggerBtn) {
    origText = triggerBtn.innerHTML;
    triggerBtn.disabled = true;
    triggerBtn.innerHTML = '<span class="spinner-inline"></span> 探测中...';
  }

  try {
    const probeRes = await apiFetch(`api/scans/${encodeURIComponent(currentTaskId)}/probe`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        subPath: child.path,
        depth: 1,
        oneFileSystem: chkOneFs.checked,
      }),
    });

    if (!probeRes.success || !probeRes.data) {
      throw new Error('子目录即时探测失败');
    }

    const probedNode = probeRes.data.node;
    if (probedNode) {
      child.children = probedNode.children || [];
      child.unrankedItems = probedNode.unrankedItems || [];
      if (probedNode.sizeBytes) child.sizeBytes = probedNode.sizeBytes;
      if (probedNode.humanSize) child.humanSize = probedNode.humanSize;
    }

    navigationStack.push(child);
    renderCurrentLevel();
  } catch (err) {
    showError(`探测子目录失败: ${err.message}`);
  } finally {
    if (triggerBtn) {
      triggerBtn.disabled = false;
      triggerBtn.innerHTML = origText;
    }
  }
}

/**
 * 渲染树状排行榜
 */
function renderTree(node) {
  treeListBody.innerHTML = '';

  const children = node.children || [];
  if (children.length === 0) {
    treeListBody.innerHTML = '<div style="padding: 24px; text-align: center; color: var(--text-muted);">此目录为空或无直接子项</div>';
    return;
  }

  children.forEach(child => {
    const row = document.createElement('div');
    row.className = `tree-row ${child.isSyntheticGroup ? 'synthetic' : ''}`;

    const icon = child.isSyntheticGroup ? '📦' : (child.isDir ? '📁' : '📄');
    const percent = child.percent || 0;

    let barColorClass = 'normal';
    if (percent > 50) barColorClass = 'danger';
    else if (percent > 20) barColorClass = 'warning';

    const hasLoadedChildren = child.isDir && !child.isSyntheticGroup && Array.isArray(child.children) && child.children.length > 0;
    const canProbe = child.isDir && !child.isSyntheticGroup && !hasLoadedChildren;

    let actionHtml = '';
    if (child.isSyntheticGroup) {
      actionHtml = `<button class="btn btn-xs btn-primary btn-view-unranked">查看明细 ➔</button>`;
    } else {
      let drillBtnHtml = '';
      if (hasLoadedChildren) {
        drillBtnHtml = `<button class="btn btn-xs btn-secondary btn-drill">下钻 ➔</button>`;
      } else if (canProbe) {
        drillBtnHtml = `<button class="btn btn-xs btn-outline-primary btn-probe-drill" title="深度即时探测此子目录">探测下钻 ⚡</button>`;
      }

      actionHtml = `
        <button class="btn-xs btn-nav-files" title="在系统文件管理器中查看">📁</button>
        ${child.isDir ? `<button class="btn-xs btn-nav-term" title="在终端中打开">💻</button>` : ''}
        ${drillBtnHtml}
      `;
    }

    row.innerHTML = `
      <div class="col-name" title="${escapeHtml(child.path)}">
        <span class="node-icon">${icon}</span>
        <span class="node-name">${escapeHtml(child.name || child.path)}</span>
        ${child.fileCount ? `<span class="node-meta">(${child.fileCount.toLocaleString()} 项)</span>` : ''}
      </div>
      <div class="col-bar">
        <div class="bar-track">
          <div class="bar-fill ${barColorClass}" style="width: ${Math.min(100, percent)}%;"></div>
        </div>
        <span class="bar-percent-text">${percent.toFixed(1)}%</span>
      </div>
      <div class="col-size">${escapeHtml(child.humanSize || formatBytes(child.sizeBytes))}</div>
      <div class="col-actions">
        ${actionHtml}
      </div>
    `;

    // 绑定合成项点击 -> 抽屉打开
    if (child.isSyntheticGroup) {
      row.addEventListener('click', () => {
        openUnrankedDrawer(node.path);
      });
      const btnView = row.querySelector('.btn-view-unranked');
      if (btnView) {
        btnView.addEventListener('click', (e) => {
          e.stopPropagation();
          openUnrankedDrawer(node.path);
        });
      }
    } else {
      // 文件定位
      const btnNavFiles = row.querySelector('.btn-nav-files');
      if (btnNavFiles) {
        btnNavFiles.addEventListener('click', (e) => {
          e.stopPropagation();
          LwtNavigationBridge.navigateToFile({
            path: child.path,
            isDir: Boolean(child.isDir),
            target: currentTarget,
          });
        });
      }

      // 终端定位
      if (child.isDir) {
        const btnNavTerm = row.querySelector('.btn-nav-term');
        if (btnNavTerm) {
          btnNavTerm.addEventListener('click', (e) => {
            e.stopPropagation();
            LwtNavigationBridge.navigateToTerminal({
              cwd: child.path,
              isDir: true,
              target: currentTarget,
            });
          });
        }
      }

      // 记忆下钻
      if (hasLoadedChildren) {
        const btnDrill = row.querySelector('.btn-drill');
        if (btnDrill) {
          btnDrill.addEventListener('click', (e) => {
            e.stopPropagation();
            navigationStack.push(child);
            renderCurrentLevel();
          });
        }
      } else if (canProbe) {
        // JIT 探测下钻
        const btnProbe = row.querySelector('.btn-probe-drill');
        if (btnProbe) {
          btnProbe.addEventListener('click', async (e) => {
            e.stopPropagation();
            await drillIntoPath(child, btnProbe);
          });
        }
      }
    }

    treeListBody.appendChild(row);
  });
}

/**
 * 绘制 Canvas 矩形树图 (Squarified Treemap)
 */
function renderTreemap(node) {
  const ctx = treemapCanvas.getContext('2d');
  const width = treemapCanvas.width;
  const height = treemapCanvas.height;

  ctx.clearRect(0, 0, width, height);

  const children = (node.children || []).filter(c => c.sizeBytes > 0);
  if (children.length === 0) {
    ctx.fillStyle = '#94a3b8';
    ctx.font = '14px sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('暂无大文件或子目录用于绘制树图', width / 2, height / 2);
    return;
  }

  // 计算相对比例
  const total = children.reduce((sum, c) => sum + c.sizeBytes, 0);
  if (total <= 0) return;

  // 简易横向比例分块算法
  const colors = [
    '#38bdf8', '#6366f1', '#10b981', '#f59e0b', '#f43f5e',
    '#8b5cf6', '#06b6d4', '#ec4899', '#14b8a6', '#f97316'
  ];

  let currentX = 0;
  let currentY = 0;
  let remainingW = width;
  let remainingH = height;

  const rects = [];

  children.forEach((child, idx) => {
    const fraction = child.sizeBytes / total;
    const color = colors[idx % colors.length];

    let w, h;
    if (remainingW > remainingH) {
      w = Math.max(2, Math.floor(remainingW * fraction));
      h = remainingH;
      rects.push({ x: currentX, y: currentY, w, h, child, color });
      currentX += w;
      remainingW = Math.max(0, remainingW - w);
    } else {
      w = remainingW;
      h = Math.max(2, Math.floor(remainingH * fraction));
      rects.push({ x: currentX, y: currentY, w, h, child, color });
      currentY += h;
      remainingH = Math.max(0, remainingH - h);
    }
  });

  // 绘制矩形
  rects.forEach(r => {
    ctx.fillStyle = r.color;
    ctx.globalAlpha = 0.85;
    ctx.fillRect(r.x + 1, r.y + 1, Math.max(1, r.w - 2), Math.max(1, r.h - 2));

    ctx.strokeStyle = '#0b0f19';
    ctx.lineWidth = 2;
    ctx.strokeRect(r.x, r.y, r.w, r.h);

    // 绘制文字
    if (r.w > 60 && r.h > 24) {
      ctx.globalAlpha = 1.0;
      ctx.fillStyle = '#ffffff';
      ctx.font = 'bold 12px sans-serif';
      ctx.textAlign = 'left';
      const label = `${r.child.name} (${r.child.humanSize || formatBytes(r.child.sizeBytes)})`;
      ctx.fillText(label, r.x + 6, r.y + 18, r.w - 12);
    }
  });

  // 绑定鼠标悬浮提示
  treemapCanvas.onmousemove = (e) => {
    const rect = treemapCanvas.getBoundingClientRect();
    const scaleX = treemapCanvas.width / rect.width;
    const scaleY = treemapCanvas.height / rect.height;
    const mouseX = (e.clientX - rect.left) * scaleX;
    const mouseY = (e.clientY - rect.top) * scaleY;

    const hit = rects.find(r => mouseX >= r.x && mouseX <= r.x + r.w && mouseY >= r.y && mouseY <= r.y + r.h);
    if (hit) {
      treemapTooltip.classList.remove('hidden');
      treemapTooltip.style.left = `${e.clientX - rect.left + 12}px`;
      treemapTooltip.style.top = `${e.clientY - rect.top + 12}px`;
      treemapTooltip.innerHTML = `
        <strong>${escapeHtml(hit.child.name)}</strong><br/>
        大小: ${hit.child.humanSize || formatBytes(hit.child.sizeBytes)}<br/>
        占比: ${(hit.child.percent || 0).toFixed(1)}%
      `;
    } else {
      treemapTooltip.classList.add('hidden');
    }
  };

  treemapCanvas.onmouseleave = () => {
    treemapTooltip.classList.add('hidden');
  };

  // 双击或单击下钻 / 抽屉查看
  treemapCanvas.onclick = async (e) => {
    const rect = treemapCanvas.getBoundingClientRect();
    const scaleX = treemapCanvas.width / rect.width;
    const scaleY = treemapCanvas.height / rect.height;
    const mouseX = (e.clientX - rect.left) * scaleX;
    const mouseY = (e.clientY - rect.top) * scaleY;

    const hit = rects.find(r => mouseX >= r.x && mouseX <= r.x + r.w && mouseY >= r.y && mouseY <= r.y + r.h);
    if (!hit) return;

    if (hit.child.isSyntheticGroup) {
      const currentNode = navigationStack[navigationStack.length - 1];
      if (currentNode) openUnrankedDrawer(currentNode.path);
      return;
    }

    if (hit.child.isDir) {
      await drillIntoPath(hit.child);
    }
  };
}

function showError(msg) {
  alertMessage.textContent = msg;
  alertBox.classList.remove('hidden');
}

function hideError() {
  alertBox.classList.add('hidden');
}

function formatBytes(bytes) {
  if (bytes <= 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
}

function escapeHtml(str) {
  if (!str) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

// 抽屉交互事件绑定
btnCloseDrawer.addEventListener('click', closeUnrankedDrawer);
unrankedDrawerBackdrop.addEventListener('click', closeUnrankedDrawer);

btnUnrankedMore.addEventListener('click', () => {
  currentUnrankedPage++;
  fetchUnrankedItems(false);
});

inputUnrankedSearch.addEventListener('input', () => {
  if (unrankedDebounceTimer) clearTimeout(unrankedDebounceTimer);
  unrankedDebounceTimer = setTimeout(() => {
    currentUnrankedKeyword = inputUnrankedSearch.value.trim();
    fetchUnrankedItems(true);
  }, 300);
});

// 事件监听注册
btnRefreshEnv.addEventListener('click', initCapabilities);
btnStartScan.addEventListener('click', startScan);
btnAbortScan.addEventListener('click', abortScan);
btnCloseAlert.addEventListener('click', hideError);

btnToggleOptions.addEventListener('click', () => {
  const isHidden = advancedPanel.classList.toggle('hidden');
  toggleArrow.textContent = isHidden ? '▼' : '▲';
});

btnViewTree.addEventListener('click', () => {
  currentView = 'tree';
  btnViewTree.classList.add('active');
  btnViewTreemap.classList.remove('active');
  viewTreeContainer.classList.remove('hidden');
  viewTreemapContainer.classList.add('hidden');
  renderCurrentLevel();
});

btnViewTreemap.addEventListener('click', () => {
  currentView = 'treemap';
  btnViewTreemap.classList.add('active');
  btnViewTree.classList.remove('active');
  viewTreemapContainer.classList.remove('hidden');
  viewTreeContainer.classList.add('hidden');
  renderCurrentLevel();
});

inputPath.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    startScan();
  }
});

// 页面加载触发环境探测
initCapabilities();
