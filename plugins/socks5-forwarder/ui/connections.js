// connections.js - 工作区 4: 实时连接与审计看板 (Live Connections & Inspection)

import { apiCall, store } from './store.js';

export async function renderConnectionsView(container) {
  container.innerHTML = `
    <div class="space-y-4 text-xs">
      <div class="flex items-center justify-between pb-2 border-b border-slate-800/80">
        <div>
          <h2 class="text-sm font-bold text-slate-100 flex items-center gap-1.5">
            <span>📊</span> 实时连接与流量审计 (Live Connections)
          </h2>
          <p class="text-xs text-slate-400">实时探测网关当前活跃的 TCP/UDP 会话、数据吞吐量与入站端口负载</p>
        </div>
        <button id="btnConnRefresh" class="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700 rounded-lg transition">
          🔄 立即刷新
        </button>
      </div>

      <div id="connStatsCards" class="grid grid-cols-1 sm:grid-cols-3 gap-3"></div>

      <div class="space-y-2">
        <h3 class="text-xs font-bold text-slate-300">当前活跃端口监听与流量分流状态</h3>
        <div id="connTableWrapper" class="overflow-x-auto rounded-xl border border-slate-800 bg-slate-900/90 shadow"></div>
      </div>
    </div>
  `;

  const loadConns = async () => {
    try {
      const res = await apiCall('connections');
      const list = res.connections || [];

      // 统计数据
      let totalActive = 0;
      let totalTx = 0;
      let totalRx = 0;
      list.forEach(c => {
        totalActive += c.activeConns || 0;
        totalTx += c.totalTx || 0;
        totalRx += c.totalRx || 0;
      });

      const statsBox = container.querySelector('#connStatsCards');
      statsBox.innerHTML = `
        <div class="p-3 rounded-xl bg-slate-900 border border-slate-800 space-y-1">
          <span class="text-[10px] text-slate-500 font-bold uppercase">当前活跃连接会话</span>
          <div class="text-xl font-bold text-cyan-400 font-mono">${totalActive}</div>
        </div>
        <div class="p-3 rounded-xl bg-slate-900 border border-slate-800 space-y-1">
          <span class="text-[10px] text-slate-500 font-bold uppercase">累计发送流量 (Tx)</span>
          <div class="text-xl font-bold text-indigo-400 font-mono">${formatBytes(totalTx)}</div>
        </div>
        <div class="p-3 rounded-xl bg-slate-900 border border-slate-800 space-y-1">
          <span class="text-[10px] text-slate-500 font-bold uppercase">累计接收流量 (Rx)</span>
          <div class="text-xl font-bold text-emerald-400 font-mono">${formatBytes(totalRx)}</div>
        </div>
      `;

      const tableBox = container.querySelector('#connTableWrapper');
      if (list.length === 0) {
        tableBox.innerHTML = `<div class="p-6 text-center text-slate-500 text-xs">当前暂无活跃的接入网关实例</div>`;
        return;
      }

      tableBox.innerHTML = `
        <table class="w-full text-left border-collapse text-[11px]">
          <thead>
            <tr class="border-b border-slate-800 bg-slate-950/80 text-slate-400">
              <th class="p-2.5">网关规则名称</th>
              <th class="p-2.5">协议与生效端口</th>
              <th class="p-2.5">挂载线路</th>
              <th class="p-2.5">实时并发</th>
              <th class="p-2.5">传输吞吐 (Tx / Rx)</th>
              <th class="p-2.5">运行状态</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-slate-800/60 font-mono">
            ${list.map(c => `
              <tr class="hover:bg-slate-800/40 transition">
                <td class="p-2.5 font-sans font-semibold text-slate-200">${c.inboundName}</td>
                <td class="p-2.5 text-cyan-300">
                  <span class="uppercase text-[9px] px-1 bg-cyan-950 rounded border border-cyan-800 font-bold mr-1">${c.protocol}</span>
                  ${(c.ports || []).map(p => `:${p}`).join(', ')}
                </td>
                <td class="p-2.5 text-indigo-300 font-sans">${store.getPipelineById(c.pipelineId)?.name || c.pipelineId}</td>
                <td class="p-2.5 font-bold ${c.activeConns > 0 ? 'text-amber-400' : 'text-slate-500'}">${c.activeConns} conn</td>
                <td class="p-2.5 text-slate-300">${formatBytes(c.totalTx)} / ${formatBytes(c.totalRx)}</td>
                <td class="p-2.5">
                  <span class="px-2 py-0.5 rounded-full text-[9px] font-bold ${c.status === 'running' ? 'bg-emerald-950 text-emerald-400 border border-emerald-800' : 'bg-slate-800 text-slate-400'}">
                    ${c.status}
                  </span>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      `;
    } catch (e) {
      console.error('Failed to load connections:', e);
    }
  };

  container.querySelector('#btnConnRefresh')?.addEventListener('click', loadConns);
  await loadConns();
}

function formatBytes(bytes) {
  if (!bytes || bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}
