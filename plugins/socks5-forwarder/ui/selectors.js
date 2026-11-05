// selectors.js - 工作区 2: 策略组极速切网器 (Proxy Selectors)

import { store, switchPipelineOutbound, pingAllNodes } from './store.js';

export function renderSelectorsView(container, drawer) {
  container.innerHTML = '';
  const pipelines = store.pipelines || [];
  const upstreams = store.upstreams || [];
  const chains = store.chains || [];

  const header = document.createElement('div');
  header.className = 'flex items-center justify-between pb-2';
  header.innerHTML = `
    <div>
      <h2 class="text-sm font-bold text-slate-100 flex items-center gap-1.5">
        <span>🎯</span> 出站策略选择矩阵 (Proxy Selectors)
      </h2>
      <p class="text-xs text-slate-400">借鉴现代代理客户端核心体验：鼠标点击任意节点胶囊，0.1 秒极速无感切换专线出口</p>
    </div>
    <div class="flex items-center gap-2">
      <button id="btnSelAddChain" class="px-3 py-1.5 text-xs font-medium bg-purple-600/30 hover:bg-purple-600/40 text-purple-300 border border-purple-500/40 rounded-lg flex items-center gap-1.5 transition">
        🔗 + 编排链式代理
      </button>
      <button id="btnSelPingAll" class="px-3 py-1.5 text-xs font-medium bg-slate-800 hover:bg-slate-700 text-cyan-400 border border-slate-700 rounded-lg flex items-center gap-1.5 transition">
        ⚡ 一键测速全部节点
      </button>
    </div>
  `;
  header.querySelector('#btnSelAddChain')?.addEventListener('click', () => drawer.openChain());
  header.querySelector('#btnSelPingAll')?.addEventListener('click', pingAllNodes);
  container.appendChild(header);

  const grid = document.createElement('div');
  grid.className = 'space-y-4';

  pipelines.forEach(pipe => {
    const groupCard = document.createElement('div');
    groupCard.className = 'p-4 rounded-xl bg-slate-900 border border-slate-800 shadow-md space-y-3';

    // 头部: 策略组名称与当前选中
    let currentTargetName = '本地直连 (DIRECT)';
    if (pipe.defaultAction === 'reject') currentTargetName = '🛑 拦截阻断 (REJECT)';
    else if (pipe.defaultOutbound) {
      const up = store.getUpstreamById(pipe.defaultOutbound);
      const ch = store.getChainById(pipe.defaultOutbound);
      if (up) currentTargetName = `📡 ${up.name}`;
      else if (ch) currentTargetName = `🔗 [链式] ${ch.name}`;
    }

    groupCard.innerHTML = `
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-2">
          <span class="text-base">🚀</span>
          <div>
            <div class="flex items-center gap-2">
              <h3 class="text-xs font-bold text-slate-100">${pipe.name}</h3>
              <span class="text-[10px] px-2 py-0.2 rounded-full bg-slate-800 text-slate-400">策略组</span>
            </div>
            <p class="text-[10px] text-slate-500">${pipe.description || '自定义分流专线'}</p>
          </div>
        </div>
        <div class="text-right">
          <span class="text-[10px] text-slate-500 block">当前出站</span>
          <span class="text-xs font-bold text-cyan-400">${currentTargetName}</span>
        </div>
      </div>

      <!-- 节点胶囊选择池 -->
      <div class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-2 pt-1">
        
        <!-- 直连与阻断快捷胶囊 -->
        <button type="button" class="btn-group-item p-2 rounded-lg text-left transition flex flex-col justify-between border ${pipe.defaultAction === 'direct' ? 'bg-emerald-950/80 border-emerald-500/80 text-emerald-300' : 'bg-slate-950/60 border-slate-800 text-slate-400 hover:border-slate-700'}" data-outbound="direct">
          <div class="flex justify-between items-center text-[10px]">
            <span class="font-bold">DIRECT</span>
            ${pipe.defaultAction === 'direct' ? '<span>✓</span>' : ''}
          </div>
          <span class="text-[10px] text-slate-500 mt-1">本地直连出网</span>
        </button>

        ${upstreams.map(u => {
          const isSelected = pipe.defaultAction === 'proxy' && pipe.defaultOutbound === u.id;
          const lat = u.latencyMs || 0;
          const isDead = u.status === 'dead';
          const latClass = isDead ? 'text-rose-400' : (lat < 200 ? 'text-emerald-400' : 'text-amber-400');
          return `
            <button type="button" class="btn-group-item p-2 rounded-lg text-left transition flex flex-col justify-between border ${isSelected ? 'bg-cyan-950/90 border-cyan-500 text-cyan-200 shadow-md shadow-cyan-950/40' : 'bg-slate-950/60 border-slate-800 text-slate-300 hover:border-slate-700'}" data-outbound="${u.id}">
              <div class="flex justify-between items-center text-[11px] font-semibold truncate">
                <span class="truncate">${u.name}</span>
                ${isSelected ? '<span class="text-cyan-400 text-xs">✓</span>' : ''}
              </div>
              <div class="flex justify-between items-center text-[9px] font-mono mt-1 pt-1 border-t border-slate-900/80">
                <span class="uppercase text-slate-500 font-bold">${u.type || 'WSS'}</span>
                <span class="${latClass}">${isDead ? '离线' : `${lat}ms`}</span>
              </div>
            </button>
          `;
        }).join('')}

        ${chains.map(c => {
          const isSelected = pipe.defaultAction === 'proxy' && pipe.defaultOutbound === c.id;
          return `
            <button type="button" class="btn-group-item p-2 rounded-lg text-left transition flex flex-col justify-between border ${isSelected ? 'bg-purple-950/90 border-purple-500 text-purple-200 shadow-md shadow-purple-950/40' : 'bg-slate-950/60 border-slate-800 text-slate-300 hover:border-slate-700'}" data-outbound="${c.id}">
              <div class="flex justify-between items-center text-[11px] font-semibold truncate">
                <span class="truncate">${c.name}</span>
                ${isSelected ? '<span class="text-purple-400 text-xs">✓</span>' : ''}
              </div>
              <div class="flex justify-between items-center text-[9px] font-mono mt-1 pt-1 border-t border-slate-900/80">
                <span class="uppercase text-purple-400 font-bold">CHAIN</span>
                <span class="text-slate-400">${c.strategy}</span>
              </div>
            </button>
          `;
        }).join('')}

      </div>
    `;

    groupCard.querySelectorAll('.btn-group-item').forEach(btn => {
      btn.addEventListener('click', async () => {
        const outId = btn.dataset.outbound;
        await switchPipelineOutbound(pipe.id, outId);
      });
    });

    grid.appendChild(groupCard);
  });

  container.appendChild(grid);
}
