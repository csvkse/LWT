// app.js - 下一代现代化 SOCKS5 控制台主入口 (Modular ESM Controller)

import { store, eventBus, refreshAllData, pingAllNodes } from './store.js';
import { ModelessDrawer } from './drawer.js';
import { renderTopologyCanvas } from './topology.js';
import { renderSelectorsView } from './selectors.js';
import { renderNodesWorkshop } from './nodes.js';
import { renderConnectionsView } from './connections.js';

let drawerInstance = null;

// 初始化控制台应用
export async function initApp() {
  drawerInstance = new ModelessDrawer();

  // 1. 注册工作区 Tab 切换
  const tabs = document.querySelectorAll('.nav-tab-btn');
  tabs.forEach(tab => {
    tab.addEventListener('click', () => {
      const target = tab.dataset.tab;
      setActiveTab(target);
    });
  });

  // 2. 注册顶部通用按钮
  document.getElementById('btnGlobalRefresh')?.addEventListener('click', () => refreshAllData());
  document.getElementById('btnGlobalPingAll')?.addEventListener('click', () => pingAllNodes());
  document.getElementById('btnQuickAddInbound')?.addEventListener('click', () => drawerInstance.openInbound());
  document.getElementById('btnQuickAddPipe')?.addEventListener('click', () => drawerInstance.openPipeline());
  document.getElementById('btnQuickAddChain')?.addEventListener('click', () => drawerInstance.openChain());
  document.getElementById('btnQuickAddNode')?.addEventListener('click', () => drawerInstance.openUpstream());
  document.getElementById('btnQuickImport')?.addEventListener('click', () => drawerInstance.openImport());
  document.getElementById('btnGlobalTunPill')?.addEventListener('click', () => drawerInstance.openTun());

  // 3. 监听全局响应式事件
  eventBus.on('data-refreshed', () => {
    renderCurrentWorkspace();
    updateGlobalTunPill();
  });

  eventBus.on('loading', (isLoading) => {
    const spinner = document.getElementById('globalSpinner');
    if (spinner) spinner.classList.toggle('hidden', !isLoading);
  });

  eventBus.on('toast', ({ type, message }) => {
    showToast(message, type);
  });

  // 4. 初次加载数据
  await refreshAllData();
  setActiveTab('topology');

  // 5. 开启 30 秒后台轻量静默轮询
  store.autoRefreshInterval = setInterval(() => {
    refreshAllData();
  }, 30000);
}

// 切换当前激活的工作区 Tab
function setActiveTab(tabKey) {
  store.activeTab = tabKey;
  document.querySelectorAll('.nav-tab-btn').forEach(btn => {
    const isActive = btn.dataset.tab === tabKey;
    btn.className = `nav-tab-btn px-3 py-1.5 rounded-lg text-xs font-semibold transition flex items-center gap-1.5 ${
      isActive 
        ? 'bg-cyan-600 text-white shadow-md shadow-cyan-600/30' 
        : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800'
    }`;
  });

  renderCurrentWorkspace();
}

// 渲染当前工作区主视口
function renderCurrentWorkspace() {
  const container = document.getElementById('mainWorkspaceContainer');
  if (!container) return;

  switch (store.activeTab) {
    case 'topology':
      renderTopologyCanvas(container, drawerInstance);
      break;
    case 'selectors':
      renderSelectorsView(container, drawerInstance);
      break;
    case 'nodes':
      renderNodesWorkshop(container, drawerInstance);
      break;
    case 'connections':
      renderConnectionsView(container);
      break;
    default:
      renderTopologyCanvas(container, drawerInstance);
      break;
  }
}

// 刷新顶部 TUN 状态胶囊
function updateGlobalTunPill() {
  const pill = document.getElementById('btnGlobalTunPill');
  if (!pill) return;
  const tun = store.tunStatus;
  const isReady = tun?.deviceReady && tun?.capNetAdminReady;
  
  pill.innerHTML = `
    <span>🛡️</span>
    <span>用户态 TUN:</span>
    <b class="${isReady ? 'text-emerald-400' : 'text-slate-500'} font-bold">${isReady ? '就绪' : '未就绪'}</b>
  `;
}

// 轻量 Toast 浮动提示组件
function showToast(message, type = 'info') {
  const container = document.getElementById('toastContainer');
  if (!container) return;

  const toast = document.createElement('div');
  const bg = type === 'error' ? 'bg-rose-950 border-rose-800 text-rose-300' : 
            (type === 'success' ? 'bg-emerald-950 border-emerald-800 text-emerald-300' : 'bg-slate-900 border-slate-700 text-cyan-300');

  toast.className = `p-3 rounded-xl border shadow-xl text-xs flex items-center gap-2 transform transition-all duration-300 translate-y-2 opacity-0 ${bg}`;
  toast.innerHTML = `<span>${type === 'error' ? '⚠️' : (type === 'success' ? '✓' : 'ℹ')}</span><span>${message}</span>`;

  container.appendChild(toast);
  void toast.offsetWidth;
  toast.classList.remove('translate-y-2', 'opacity-0');

  setTimeout(() => {
    toast.classList.add('opacity-0', 'translate-y-2');
    setTimeout(() => toast.remove(), 300);
  }, 3200);
}

// DOM 加载就绪后启动应用
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initApp);
} else {
  initApp();
}
