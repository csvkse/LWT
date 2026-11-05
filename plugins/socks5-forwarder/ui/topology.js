// topology.js - 工作区 1: 流式拓扑管道工作台 (Topology Flow Canvas & Standby Inbound Pool)

import { store, switchPipelineOutbound, apiCall, refreshAllData, eventBus } from './store.js';

export function renderTopologyCanvas(container, drawer) {
  container.innerHTML = '';
  const pipelines = store.pipelines || [];
  const inbounds = store.inbounds || [];

  const wrapper = document.createElement('div');
  wrapper.className = 'space-y-4';

  // 1. 待命接入端点池 (Standby Inbound Pool): 展示未绑定任何 Pipeline 的端口网关，彻底消灭孤儿黑洞
  const standbyInbounds = inbounds.filter(inb => !inb.pipelineId);
  if (standbyInbounds.length > 0) {
    const standbyPool = document.createElement('div');
    standbyPool.className = 'p-3 rounded-xl bg-amber-950/30 border border-amber-900/60 shadow space-y-2';
    standbyPool.innerHTML = `
      <div class="flex items-center justify-between text-xs">
        <span class="font-bold text-amber-300 flex items-center gap-1.5">
          <span>💤</span> 待命接入网关池 (Standby Inbounds - 尚未挂载分流线路, 共 ${standbyInbounds.length} 个)
        </span>
        <span class="text-[10px] text-amber-400/80">物理端口监听正常，选择线路即可一键连线出海</span>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-2 pt-1">
        ${standbyInbounds.map(inb => `
          <div class="p-2 rounded-lg bg-slate-900/90 border border-slate-800 flex items-center justify-between gap-2 text-xs">
            <div class="truncate">
              <span class="font-semibold text-slate-200">${inb.name}</span>
              <span class="font-mono text-cyan-400 font-bold ml-1">:${inb.listenPort}</span>
            </div>
            <div class="flex items-center gap-1">
              <select class="sel-bind-standby bg-slate-800 border border-slate-700 rounded px-1.5 py-0.5 text-indigo-300 text-[10px] focus:outline-none" data-inbound="${inb.id}">
                <option value="">挂载到线路 ▾</option>
                ${pipelines.map(p => `<option value="${p.id}">🔀 ${p.name}</option>`).join('')}
              </select>
              <button class="btn-del-standby text-slate-500 hover:text-rose-400 p-1 text-xs transition" data-inbound="${inb.id}" title="彻底销毁此待命网关">🗑</button>
            </div>
          </div>
        `).join('')}
      </div>
    `;

    // 绑定待命池挂载与删除事件
    standbyPool.querySelectorAll('.sel-bind-standby').forEach(sel => {
      sel.addEventListener('change', async (e) => {
        const pipeId = e.target.value;
        const inbId = sel.dataset.inbound;
        if (!pipeId) return;
        const targetInb = inbounds.find(i => i.id === inbId);
        if (!targetInb) return;

        targetInb.pipelineId = pipeId;
        try {
          await apiCall('inbounds', { method: 'POST', body: targetInb });
          await refreshAllData();
          eventBus.emit('toast', { type: 'success', message: `端口 :${targetInb.listenPort} 已成功挂载至线路！` });
        } catch (err) {
          alert('挂载失败: ' + err.message);
        }
      });
    });

    standbyPool.querySelectorAll('.btn-del-standby').forEach(btn => {
      btn.addEventListener('click', async () => {
        const inbId = btn.dataset.inbound;
        const targetInb = inbounds.find(i => i.id === inbId);
        if (!confirm(`确定彻底删除待命端口 [${targetInb?.name || inbId}] 吗？`)) return;
        try {
          await apiCall(`inbounds?id=${encodeURIComponent(inbId)}`, { method: 'DELETE' });
          await refreshAllData();
          eventBus.emit('toast', { type: 'success', message: '待命网关已销毁' });
        } catch (err) {
          alert('删除失败: ' + err.message);
        }
      });
    });

    wrapper.appendChild(standbyPool);
  }

  // 2. 流水线主画布
  if (pipelines.length === 0) {
    wrapper.innerHTML += `
      <div class="p-8 text-center bg-slate-900/60 rounded-xl border border-slate-800 space-y-3">
        <div class="text-3xl">🔀</div>
        <div class="text-sm font-bold text-slate-300">尚未初始化分流流水线</div>
        <p class="text-xs text-slate-500">点击下方按钮快速创建你的第一条流量管道</p>
        <button id="btnEmptyAddPipe" class="px-4 py-2 bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-semibold rounded-lg shadow">
          + 新建分流流水线
        </button>
      </div>
    `;
    container.appendChild(wrapper);
    container.querySelector('#btnEmptyAddPipe')?.addEventListener('click', () => drawer.openPipeline());
    return;
  }

  pipelines.forEach((pipe) => {
    const boundInbounds = inbounds.filter(inb => inb.pipelineId === pipe.id);
    
    let currentOutboundText = '本地直连 (DIRECT)';
    let currentOutboundLatency = 0;

    if (pipe.defaultAction === 'reject') {
      currentOutboundText = '🛑 全部阻断 (REJECT)';
    } else if (pipe.defaultAction === 'direct') {
      currentOutboundText = '⚡ 本地网络直连 (DIRECT)';
    } else if (pipe.defaultOutbound) {
      const up = store.getUpstreamById(pipe.defaultOutbound);
      const ch = store.getChainById(pipe.defaultOutbound);
      if (up) {
        currentOutboundText = `${up.name} (${up.server || 'WSS'}:${up.port || ''})`;
        currentOutboundLatency = up.latencyMs || 0;
      } else if (ch) {
        currentOutboundText = `🔗 [链式] ${ch.name}`;
      }
    }

    const card = document.createElement('div');
    card.className = 'p-4 rounded-xl bg-slate-900/90 border border-slate-800 shadow-lg space-y-3 relative overflow-hidden group hover:border-slate-700 transition';

    card.innerHTML = `
      <!-- 卡片头部: 线路名称与快速操作 -->
      <div class="flex items-center justify-between pb-2 border-b border-slate-800/80">
        <div class="flex items-center gap-2">
          <span class="w-2.5 h-2.5 rounded-full ${pipe.defaultAction === 'reject' ? 'bg-rose-500' : 'bg-emerald-400'} animate-pulse"></span>
          <h3 class="text-sm font-bold text-slate-100 flex items-center gap-2">
            ${pipe.name}
            ${pipe.id === 'pipe-default' ? `<span class="px-1.5 py-0.2 rounded text-[10px] bg-slate-800 text-slate-400 font-normal">默认线路</span>` : ''}
          </h3>
          <span class="text-xs text-slate-500">${pipe.description || ''}</span>
        </div>
        <div class="flex items-center gap-1.5">
          <button class="btn-edit-pipe px-2.5 py-1 rounded bg-slate-800 hover:bg-slate-700 text-indigo-400 text-xs flex items-center gap-1 transition" title="编辑此分流线路">
            <span>⚙</span> 编排线路
          </button>
          ${pipe.id !== 'pipe-default' ? `
            <button class="btn-del-pipe px-2 py-1 rounded bg-slate-800 hover:bg-rose-900/50 text-rose-400 text-xs transition" title="删除此分流流水线">
              🗑
            </button>
          ` : ''}
        </div>
      </div>

      <!-- 水平拓扑流动画布 -->
      <div class="grid grid-cols-1 md:grid-cols-4 gap-3 items-center text-xs">
        
        <!-- 阶段 1: 流量接入端点 (Inbounds) -->
        <div class="p-3 rounded-lg bg-slate-950 border border-slate-800/80 space-y-1.5">
          <div class="text-[10px] text-slate-500 font-bold uppercase tracking-wider flex justify-between items-center">
            <span>1. 流量接入 (Inbound)</span>
            <span class="text-cyan-400 cursor-pointer hover:underline btn-add-inbound">+ 绑定端口</span>
          </div>
          ${boundInbounds.length === 0 ? `
            <div class="text-[11px] text-slate-500 italic py-1">暂无端口绑定该线路</div>
          ` : boundInbounds.map(inb => `
            <div class="p-1 rounded bg-slate-900/60 border border-slate-900 flex items-center justify-between text-[11px] group/item hover:border-slate-800">
              <span class="font-semibold text-slate-200 cursor-pointer hover:text-cyan-400 btn-edit-inbound truncate max-w-[100px]" data-id="${inb.id}" title="${inb.name}">
                ${inb.name}
              </span>
              <div class="flex items-center gap-1">
                <span class="font-mono text-cyan-300 font-bold bg-cyan-950/80 px-1 py-0.2 rounded border border-cyan-800/60 text-[10px]">
                  :${inb.listenPort}
                </span>
                <!-- 解绑微按钮 -->
                <button type="button" class="btn-unbind-inbound text-slate-500 hover:text-amber-400 px-1 py-0.5 text-[10px] transition rounded hover:bg-slate-800" data-id="${inb.id}" title="从当前线路解绑 (移至待命池)">
                  [-]
                </button>
                <!-- 彻底删除微按钮 -->
                <button type="button" class="btn-del-inbound text-slate-500 hover:text-rose-400 px-1 py-0.5 text-[10px] transition rounded hover:bg-slate-800" data-id="${inb.id}" title="彻底删除此端口监听">
                  🗑
                </button>
              </div>
            </div>
          `).join('')}
        </div>

        <!-- 阶段 2: 分流规则决策引擎 (Routing Rules) -->
        <div class="p-3 rounded-lg bg-slate-950 border border-slate-800/80 space-y-1.5 cursor-pointer hover:border-indigo-500/50 transition btn-edit-pipe">
          <div class="text-[10px] text-slate-500 font-bold uppercase tracking-wider flex justify-between">
            <span>2. 智能分流 (Rules)</span>
            <span class="text-indigo-400">${(pipe.rules || []).length} 条规则</span>
          </div>
          <div class="flex flex-wrap gap-1 max-h-16 overflow-hidden">
            ${(pipe.rules || []).slice(0, 3).map(r => `
              <span class="px-1.5 py-0.5 rounded text-[10px] bg-slate-800 border border-slate-700 text-slate-300 truncate max-w-[120px]">
                ${r.value} ➔ <b class="${r.action === 'direct' ? 'text-emerald-400' : (r.action === 'reject' ? 'text-rose-400' : 'text-cyan-400')}">${r.action}</b>
              </span>
            `).join('')}
            ${(pipe.rules || []).length > 3 ? `<span class="text-[10px] text-slate-500 self-center">+${pipe.rules.length - 3}条</span>` : ''}
            ${(!pipe.rules || pipe.rules.length === 0) ? `<span class="text-[11px] text-slate-500 italic">全部流量遵循默认动作</span>` : ''}
          </div>
          <div class="text-[10px] text-slate-500 flex items-center gap-1 pt-1 border-t border-slate-900">
            <span>局域网私网直连:</span>
            <b class="${pipe.directPrivateNetwork !== false ? 'text-emerald-400' : 'text-slate-500'}">${pipe.directPrivateNetwork !== false ? '已开启' : '关闭'}</b>
          </div>
        </div>

        <!-- 阶段 3: 出站策略选择器 (Proxy Selector 极速切换) -->
        <div class="p-3 rounded-lg bg-slate-950 border border-slate-800/80 space-y-1.5 md:col-span-2">
          <div class="text-[10px] text-slate-500 font-bold uppercase tracking-wider flex justify-between items-center">
            <span>3. 落地出站选择器 (Proxy Selector - 点击即切)</span>
            <span class="text-[10px] text-emerald-400 font-mono">0.1s 极速生效</span>
          </div>
          <div class="flex flex-wrap gap-1.5 pt-0.5">
            <button type="button" class="btn-select-outbound px-2.5 py-1 rounded-lg text-[11px] flex items-center gap-1.5 transition ${pipe.defaultAction === 'direct' ? 'bg-emerald-600 text-white font-bold shadow-md shadow-emerald-600/30' : 'bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700'}" data-pipe="${pipe.id}" data-target="direct">
              <span>${pipe.defaultAction === 'direct' ? '✓' : '⚡'}</span>
              <span>DIRECT (直连)</span>
            </button>
            ${store.upstreams.map(u => {
              const isSelected = pipe.defaultAction === 'proxy' && pipe.defaultOutbound === u.id;
              const lat = u.latencyMs || 0;
              const latColor = u.status === 'dead' ? 'text-rose-400' : (lat < 200 ? 'text-emerald-400' : 'text-amber-400');
              return `
                <button type="button" class="btn-select-outbound px-2.5 py-1 rounded-lg text-[11px] flex items-center gap-1.5 transition ${isSelected ? 'bg-cyan-600 text-white font-bold shadow-md shadow-cyan-600/30 border border-cyan-400' : 'bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700'}" data-pipe="${pipe.id}" data-target="${u.id}">
                  <span>${isSelected ? '✓' : '•'}</span>
                  <span>${u.name}</span>
                  <span class="font-mono text-[9px] px-1 rounded bg-black/40 ${latColor}">${u.status === 'dead' ? '离线' : `${lat}ms`}</span>
                </button>
              `;
            }).join('')}
            ${store.chains.map(c => {
              const isSelected = pipe.defaultAction === 'proxy' && pipe.defaultOutbound === c.id;
              return `
                <button type="button" class="btn-select-outbound px-2.5 py-1 rounded-lg text-[11px] flex items-center gap-1.5 transition ${isSelected ? 'bg-purple-600 text-white font-bold shadow-md shadow-purple-600/30 border border-purple-400' : 'bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700'}" data-pipe="${pipe.id}" data-target="${c.id}">
                  <span>${isSelected ? '✓' : '🔗'}</span>
                  <span>[链式] ${c.name}</span>
                </button>
              `;
            }).join('')}
          </div>
          <div class="text-[11px] text-slate-400 pt-1 border-t border-slate-900 flex items-center justify-between">
            <div class="flex items-center gap-1.5 truncate">
              <span class="text-slate-500">实际出口:</span>
              <span class="font-semibold text-slate-200 truncate">${currentOutboundText}</span>
              ${currentOutboundLatency > 0 ? `<span class="px-1.5 py-0.2 rounded bg-emerald-950 text-emerald-400 font-mono text-[10px] border border-emerald-800/40">${currentOutboundLatency}ms</span>` : ''}
            </div>
          </div>
        </div>

      </div>
    `;

    // 绑定事件
    card.querySelectorAll('.btn-edit-pipe').forEach(btn => {
      btn.addEventListener('click', () => drawer.openPipeline(pipe));
    });

    card.querySelector('.btn-del-pipe')?.addEventListener('click', async () => {
      if (!confirm(`确定删除分流线路 [${pipe.name}] 吗？\n删除后其挂载的端口将自动安全移入待命池。`)) return;
      try {
        // 级联安全自愈：将该线路挂载的 Inbounds 先解绑
        for (const inb of boundInbounds) {
          inb.pipelineId = '';
          await apiCall('inbounds', { method: 'POST', body: inb });
        }
        await apiCall(`pipelines?id=${encodeURIComponent(pipe.id)}`, { method: 'DELETE' });
        await refreshAllData();
        eventBus.emit('toast', { type: 'success', message: `线路 [${pipe.name}] 已成功删除` });
      } catch (err) {
        alert('删除线路失败: ' + err.message);
      }
    });

    card.querySelector('.btn-add-inbound')?.addEventListener('click', (e) => {
      e.stopPropagation();
      drawer.openInbound({ pipelineId: pipe.id });
    });

    card.querySelectorAll('.btn-edit-inbound').forEach(el => {
      el.addEventListener('click', (e) => {
        e.stopPropagation();
        const id = el.dataset.id;
        const inb = inbounds.find(i => i.id === id);
        if (inb) drawer.openInbound(inb);
      });
    });

    // 阶段 1 解绑微按钮: 将 pipelineId 置空，进入待命池
    card.querySelectorAll('.btn-unbind-inbound').forEach(btn => {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        const id = btn.dataset.id;
        const targetInb = inbounds.find(i => i.id === id);
        if (!targetInb) return;
        if (!confirm(`确定从线路 [${pipe.name}] 解绑端口 :${targetInb.listenPort} 吗？\n解绑后端口仍会保持监听，并安全存入待命池。`)) return;

        targetInb.pipelineId = '';
        try {
          await apiCall('inbounds', { method: 'POST', body: targetInb });
          await refreshAllData();
          eventBus.emit('toast', { type: 'success', message: `端口 :${targetInb.listenPort} 已解绑，移入待命池` });
        } catch (err) {
          alert('解绑失败: ' + err.message);
        }
      });
    });

    // 阶段 1 彻底销毁微按钮
    card.querySelectorAll('.btn-del-inbound').forEach(btn => {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        const id = btn.dataset.id;
        const targetInb = inbounds.find(i => i.id === id);
        if (!targetInb) return;
        if (!confirm(`确定彻底销毁网关 [${targetInb.name}] (监听端口 :${targetInb.listenPort}) 吗？`)) return;

        try {
          await apiCall(`inbounds?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
          await refreshAllData();
          eventBus.emit('toast', { type: 'success', message: `网关 [${targetInb.name}] 已彻底删除` });
        } catch (err) {
          alert('删除失败: ' + err.message);
        }
      });
    });

    card.querySelectorAll('.btn-select-outbound').forEach(btn => {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        const pipeId = btn.dataset.pipe;
        const targetId = btn.dataset.target;
        await switchPipelineOutbound(pipeId, targetId);
      });
    });

    wrapper.appendChild(card);
  });

  container.appendChild(wrapper);
}
