// nodes.js - 工作区 3: 节点工坊与测速 (Node Workshop)

import { store, apiCall, refreshAllData, pingAllNodes, eventBus } from './store.js';

export function renderNodesWorkshop(container, drawer) {
  container.innerHTML = '';
  const upstreams = store.upstreams || [];

  const header = document.createElement('div');
  header.className = 'flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-2 border-b border-slate-800/80';
  header.innerHTML = `
    <div>
      <h2 class="text-sm font-bold text-slate-100 flex items-center gap-1.5">
        <span>📡</span> 远端服务节点工坊 (Node Workshop)
      </h2>
      <p class="text-xs text-slate-400">管理多协议出口节点（VLESS Vision / Trojan / Shadowsocks / WSS / SOCKS5 / HTTP），支持并发测速与自愈清理</p>
    </div>
    <div class="flex items-center gap-2 flex-wrap">
      <button id="btnNodePingAll" class="px-3 py-1.5 text-xs font-medium bg-slate-800 hover:bg-slate-700 text-cyan-400 border border-slate-700 rounded-lg flex items-center gap-1 transition">
        ⚡ 并发测速
      </button>
      <button id="btnCleanDead" class="px-3 py-1.5 text-xs font-medium bg-rose-950/60 hover:bg-rose-900/60 text-rose-300 border border-rose-800/60 rounded-lg flex items-center gap-1 transition">
        🧹 清理离线节点
      </button>
      <button id="btnImportNodes" class="px-3 py-1.5 text-xs font-medium bg-emerald-600/30 hover:bg-emerald-600/40 text-emerald-300 border border-emerald-500/40 rounded-lg flex items-center gap-1 transition">
        📥 批量导入
      </button>
      <button id="btnAddSingleNode" class="px-3 py-1.5 text-xs font-medium bg-cyan-600 hover:bg-cyan-500 text-white rounded-lg flex items-center gap-1 shadow-md shadow-cyan-600/30 transition">
        + 手动添加节点
      </button>
    </div>
  `;

  header.querySelector('#btnNodePingAll')?.addEventListener('click', pingAllNodes);
  header.querySelector('#btnImportNodes')?.addEventListener('click', () => drawer.openImport());
  header.querySelector('#btnAddSingleNode')?.addEventListener('click', () => drawer.openUpstream());

  header.querySelector('#btnCleanDead')?.addEventListener('click', async () => {
    const deadNodes = upstreams.filter(u => u.status === 'dead' && u.id !== 'default-lwt-node');
    if (deadNodes.length === 0) {
      alert('当前没有需要清理的离线失效节点！');
      return;
    }
    if (!confirm(`扫描到 ${deadNodes.length} 个离线节点，确定一键清理吗？`)) return;

    for (const d of deadNodes) {
      try {
        await apiCall(`upstreams?id=${encodeURIComponent(d.id)}`, { method: 'DELETE' });
      } catch (_) {}
    }
    await refreshAllData();
    eventBus.emit('toast', { type: 'success', message: `已清理 ${deadNodes.length} 个失效节点` });
  });

  container.appendChild(header);

  // 节点卡片网格
  const grid = document.createElement('div');
  grid.className = 'grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 pt-2';

  if (upstreams.length === 0) {
    grid.innerHTML = `
      <div class="col-span-full p-8 text-center bg-slate-900/60 rounded-xl border border-slate-800 space-y-2">
        <div class="text-2xl">📡</div>
        <div class="text-xs text-slate-400">节点库暂为空白，点击上方“批量导入”或“手动添加”</div>
      </div>
    `;
    container.appendChild(grid);
    return;
  }

  upstreams.forEach(node => {
    const isLwt = node.type === 'lwt_wss' || (!node.type && node.url);
    const isVless = node.type === 'vless';
    const isTrojan = node.type === 'trojan';
    const isSs = node.type === 'shadowsocks';

    let protoBadge = `<span class="px-1.5 py-0.2 rounded text-[10px] font-bold bg-cyan-950 text-cyan-400 border border-cyan-800">SOCKS5</span>`;
    if (isLwt) protoBadge = `<span class="px-1.5 py-0.2 rounded text-[10px] font-bold bg-indigo-950 text-indigo-400 border border-indigo-800">LWT-WSS</span>`;
    else if (isVless) protoBadge = `<span class="px-1.5 py-0.2 rounded text-[10px] font-bold bg-emerald-950 text-emerald-400 border border-emerald-800">VLESS</span>`;
    else if (isTrojan) protoBadge = `<span class="px-1.5 py-0.2 rounded text-[10px] font-bold bg-purple-950 text-purple-400 border border-purple-800">TROJAN</span>`;
    else if (isSs) protoBadge = `<span class="px-1.5 py-0.2 rounded text-[10px] font-bold bg-amber-950 text-amber-400 border border-amber-800">SS</span>`;

    const isHealthy = node.status === 'healthy';
    const isDead = node.status === 'dead';
    const statusDot = isHealthy ? '🟢' : (isDead ? '🔴' : '⚪');
    const latText = isDead ? '离线' : (node.latencyMs ? `${node.latencyMs} ms` : '未测速');
    const latColor = isHealthy ? 'text-emerald-400' : (isDead ? 'text-rose-400' : 'text-slate-400');

    const card = document.createElement('div');
    card.className = 'p-3.5 rounded-xl bg-slate-900 border border-slate-800 shadow space-y-2.5 relative group hover:border-slate-700 transition flex flex-col justify-between';

    card.innerHTML = `
      <div class="space-y-1.5">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-1.5">
            ${protoBadge}
            <h4 class="text-xs font-bold text-slate-100 truncate max-w-[140px]">${node.name}</h4>
          </div>
          <span class="text-[11px] font-mono font-bold ${latColor} flex items-center gap-1">
            <span>${statusDot}</span> ${latText}
          </span>
        </div>

        <div class="text-[11px] font-mono text-slate-400 truncate bg-slate-950/60 p-1.5 rounded border border-slate-900">
          ${node.url || `${node.server}:${node.port}`}
        </div>

        ${node.lastError ? `
          <div class="text-[10px] text-rose-400 font-mono bg-rose-950/30 p-1 rounded border border-rose-900/40 truncate" title="${node.lastError}">
            ⚠️ ${node.lastError}
          </div>
        ` : ''}
      </div>

      <div class="flex items-center justify-between pt-2 border-t border-slate-800/80 text-[11px]">
        <span class="text-[10px] text-slate-500">${node.skipTlsVerify ? '⚠️ 免证书校验' : '🔒 TLS 校验'}</span>
        <div class="flex items-center gap-1.5">
          <button class="btn-single-ping px-2 py-0.8 bg-slate-800 hover:bg-slate-700 text-cyan-400 rounded transition" title="即时测速">
            ⚡ 测速
          </button>
          <button class="btn-edit-node px-2 py-0.8 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded transition" title="编辑节点参数">
            ⚙
          </button>
          ${node.id !== 'default-lwt-node' ? `
            <button class="btn-del-node px-2 py-0.8 bg-slate-800 hover:bg-rose-900/40 text-rose-400 rounded transition" title="直接删除此节点">
              🗑
            </button>
          ` : ''}
        </div>
      </div>
    `;

    card.querySelector('.btn-single-ping')?.addEventListener('click', async () => {
      const pingBtn = card.querySelector('.btn-single-ping');
      pingBtn.textContent = '⏳';
      try {
        await apiCall(`upstreams/test?id=${encodeURIComponent(node.id)}`, { method: 'POST' });
        await refreshAllData();
      } catch (e) {
        alert('测速失败: ' + e.message);
      } finally {
        pingBtn.textContent = '⚡ 测速';
      }
    });

    card.querySelector('.btn-edit-node')?.addEventListener('click', () => {
      drawer.openUpstream(node);
    });

    card.querySelector('.btn-del-node')?.addEventListener('click', async () => {
      const usingPipelines = (store.pipelines || []).filter(p => p.defaultOutbound === node.id);
      let warn = `确定删除节点 [${node.name}] 吗？`;
      if (usingPipelines.length > 0) {
        warn = `⚠️ 警告：该节点正被线路 [${usingPipelines.map(p => p.name).join(', ')}] 设为出口，删除后这些线路将自动降级为直连出网！\n\n确定继续删除吗？`;
      }
      if (!confirm(warn)) return;
      try {
        await apiCall(`upstreams?id=${encodeURIComponent(node.id)}`, { method: 'DELETE' });
        await refreshAllData();
        eventBus.emit('toast', { type: 'success', message: `节点 [${node.name}] 已删除` });
      } catch (err) {
        alert('删除失败: ' + err.message);
      }
    });

    grid.appendChild(card);
  });

  container.appendChild(grid);
}
