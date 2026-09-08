import { defineComponent, onMounted, onUnmounted, ref, watch, nextTick } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { formatBps, formatBytes, formatTime, formatUptime, usageColor } from '../utils/format.js';

const RANGES = [
  { label: '1 小时', hours: 1 },
  { label: '6 小时', hours: 6 },
  { label: '24 小时', hours: 24 },
  { label: '7 天', hours: 168 },
];

const USAGE_SERIES = [
  {},
  { label: 'CPU%', stroke: '#22d3ee', width: 1.5, fill: 'rgba(34,211,238,0.08)' },
  { label: '内存%', stroke: '#a78bfa', width: 1.5, fill: 'rgba(167,139,250,0.08)' },
  { label: '根分区%', stroke: '#34d399', width: 1.5 },
];

const NET_SERIES = [
  {},
  { label: '下载', stroke: '#34d399', width: 1.5 },
  { label: '上传', stroke: '#fbbf24', width: 1.5 },
];

const DISK_COLORS = ['#34d399', '#fbbf24', '#22d3ee', '#a78bfa', '#fb7185', '#38bdf8', '#f97316', '#a3e635'];

const PROCESS_SORTS = [
  { value: 'cpu', label: '按 CPU' },
  { value: 'mem', label: '按内存' },
  { value: 'diskRead', label: '磁盘读' },
  { value: 'diskWrite', label: '磁盘写' },
  { value: 'netSend', label: '网络发' },
  { value: 'netRecv', label: '网络收' },
];

const CHART_OPTIONS = {
  width: 600,
  height: 220,
  cursor: { x: false, y: false, lock: true },
  legend: { show: true, live: false },
  scales: { x: { time: true } },
  axes: [
    { stroke: '#475569', grid: { stroke: 'rgba(29,44,74,0.6)' }, ticks: { stroke: 'rgba(29,44,74,0.6)' } },
    { stroke: '#475569', grid: { stroke: 'rgba(29,44,74,0.6)' }, ticks: { stroke: 'rgba(29,44,74,0.6)' } },
  ],
};

export default defineComponent({
  name: 'SystemStatusView',
  setup() {
    const status = ref(null);
    const loading = ref(false);
    const autoRefresh = ref(true);
    const historyRange = ref(6);
    const usageChartEl = ref(null);
    const netChartEl = ref(null);
    const diskChartEl = ref(null);
    const processPoints = ref([]);
    const selectedPoint = ref(null);
    const processSort = ref('cpu');
    const overviewProcessSort = ref('cpu');
    let usageChart = null;
    let netChart = null;
    let diskChart = null;
    let refreshTimer = null;

    async function load() {
      loading.value = true;
      try {
        const result = await http(API.systemStatus.current);
        if (result.ok) status.value = result.data;
      } finally {
        loading.value = false;
      }
    }

    function toPoints(rows) {
      const times = rows.map((r) => new Date(r.time).getTime() / 1000);
      return times;
    }

    function renderUsageChart(rows) {
      const el = usageChartEl.value;
      if (!el || typeof window.uPlot === 'undefined') return;
      const times = toPoints(rows);
      const data = [
        times,
        rows.map((r) => r.cpuUsage),
        rows.map((r) => r.memUsage),
        rows.map((r) => r.diskRootUsage),
      ];
      const options = {
        ...CHART_OPTIONS,
        width: el.clientWidth || 600,
        series: USAGE_SERIES,
        axes: [
          CHART_OPTIONS.axes[0],
          { ...CHART_OPTIONS.axes[1], values: (_u, vals) => vals.map((v) => `${Math.round(v)}%`) },
        ],
      };
      if (usageChart) usageChart.destroy();
      usageChart = new window.uPlot(options, data, el);
    }

    function renderNetChart(rows) {
      const el = netChartEl.value;
      if (!el || typeof window.uPlot === 'undefined') return;
      const times = toPoints(rows);
      const data = [
        times,
        rows.map((r) => r.recvBytesPerSec / 1024),
        rows.map((r) => r.sentBytesPerSec / 1024),
      ];
      const options = {
        ...CHART_OPTIONS,
        width: el.clientWidth || 600,
        series: NET_SERIES,
        axes: [
          CHART_OPTIONS.axes[0],
          { ...CHART_OPTIONS.axes[1], values: (_u, vals) => vals.map((v) => `${v.toFixed(0)} KB/s`) },
        ],
      };
      if (netChart) netChart.destroy();
      netChart = new window.uPlot(options, data, el);
    }

    // 磁盘：每个挂载点一条使用率曲线（按名称分组取最新值）
    function renderDiskChart(rows) {
      const el = diskChartEl.value;
      if (!el || typeof window.uPlot === 'undefined') return;
      const mounts = [...new Set(rows.map((r) => r.mount))];
      if (!mounts.length) {
        if (diskChart) diskChart.destroy();
        return;
      }
      const times = [...new Set(rows.map((r) => new Date(r.time).getTime() / 1000))].sort((a, b) => a - b);
      const series = [{}, ...mounts.map((m, i) => ({ label: m, stroke: DISK_COLORS[i % DISK_COLORS.length], width: 1.5 }))];
      const data = [times];
      for (const mount of mounts) {
        const byTime = new Map(rows.filter((r) => r.mount === mount).map((r) => [new Date(r.time).getTime() / 1000, r.usagePercent]));
        data.push(times.map((t) => byTime.get(t) ?? null));
      }
      const options = {
        ...CHART_OPTIONS,
        width: el.clientWidth || 600,
        series,
        axes: [
          CHART_OPTIONS.axes[0],
          { ...CHART_OPTIONS.axes[1], values: (_u, vals) => vals.map((v) => `${Math.round(v)}%`) },
        ],
      };
      if (diskChart) diskChart.destroy();
      diskChart = new window.uPlot(options, data, el);
    }

    // 采样点：按时间戳对齐 进程/磁盘/网络/整机 四类数据，每个点可展开查看当时的进程、磁盘、网络。
    function buildPoints(systemRows, diskRows, netRows, processRows) {
      const byTime = new Map();
      const group = (rows) => {
        for (const r of rows) {
          const key = new Date(r.time).getTime();
          if (!byTime.has(key)) byTime.set(key, { time: r.time, processes: [], disks: [], networks: [], system: null });
          const point = byTime.get(key);
          if (r.mount !== undefined) point.disks.push(r);
          else if (r.name !== undefined && r.sentBytesPerSec !== undefined && r.recvBytesPerSec !== undefined) point.networks.push(r);
          else if (r.cpuUsage !== undefined) point.system = r;
          else point.processes.push(r);
        }
      };
      group(systemRows || []);
      group(diskRows || []);
      group(netRows || []);
      group(processRows || []);
      // 仅保留有进程数据的点（进程采样点即资源采样时点），时间正序。
      return [...byTime.values()]
        .filter((p) => p.processes.length > 0)
        .sort((a, b) => new Date(a.time) - new Date(b.time));
    }

    function sortedProcesses(procs, sort) {
      const list = [...procs];
      if (sort === 'cpu') list.sort((a, b) => b.cpuPercent - a.cpuPercent);
      else if (sort === 'mem') list.sort((a, b) => b.memBytes - a.memBytes);
      else if (sort === 'diskRead') list.sort((a, b) => b.diskReadBps - a.diskReadBps);
      else if (sort === 'diskWrite') list.sort((a, b) => b.diskWriteBps - a.diskWriteBps);
      else if (sort === 'netSend') list.sort((a, b) => b.netSentBps - a.netSentBps);
      else if (sort === 'netRecv') list.sort((a, b) => b.netRecvBps - a.netRecvBps);
      return list;
    }

    // 即时进程 TOP：合并 CPU / 内存两个 Top 列表（去重），按所选维度排序展示。
    function overviewProcesses(status, sort) {
      if (!status) return [];
      const seen = new Map();
      for (const p of [...(status.topCpuProcesses || []), ...(status.topMemProcesses || [])]) {
        if (!seen.has(p.pid)) seen.set(p.pid, p);
      }
      return sortedProcesses([...seen.values()], sort);
    }

    function selectPoint(p) {
      selectedPoint.value = p;
    }

    async function loadHistory() {
      const result = await http(API.systemStatus.resourceHistory, { params: { hours: historyRange.value } });
      if (result.ok) {
        const data = result.data;
        await nextTick();
        renderUsageChart(data.system || []);
        renderNetChart(data.networks || []);
        renderDiskChart(data.disks || []);
        processPoints.value = buildPoints(data.system, data.disks, data.networks, data.processes);
        if (selectedPoint.value) {
          const found = processPoints.value.find((p) => new Date(p.time).getTime() === new Date(selectedPoint.value.time).getTime());
          if (!found) selectedPoint.value = null;
          else selectedPoint.value = found;
        }
      }
    }

    function setRange(hours) {
      historyRange.value = hours;
      loadHistory();
    }

    function toggleAutoRefresh(enabled) {
      if (refreshTimer) {
        window.clearInterval(refreshTimer);
        refreshTimer = null;
      }
      if (enabled) {
        refreshTimer = window.setInterval(() => {
          load();
          loadHistory();
        }, 10000);
      }
    }

    watch(autoRefresh, (enabled) => toggleAutoRefresh(enabled));

    // 窗口宽度变化（手机旋转 / 缩放）时按新宽度重绘图表
    function handleResize() {
      if (usageChart || netChart || diskChart) loadHistory();
    }

    onMounted(async () => {
      await load();
      await loadHistory();
      toggleAutoRefresh(autoRefresh.value);
      window.addEventListener('resize', handleResize);
    });

    onUnmounted(() => {
      if (refreshTimer) window.clearInterval(refreshTimer);
      window.removeEventListener('resize', handleResize);
      if (usageChart) usageChart.destroy();
      if (netChart) netChart.destroy();
      if (diskChart) diskChart.destroy();
    });

    return {
      status, loading, autoRefresh, historyRange, usageChartEl, netChartEl, diskChartEl,
      processPoints, selectedPoint, processSort, PROCESS_SORTS,
      overviewProcessSort, overviewProcesses,
      load, loadHistory, setRange, selectPoint, sortedProcesses, RANGES,
      formatBps, formatBytes, formatTime, formatUptime, usageColor,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-center gap-2 flex-wrap">
        <h2 class="text-sm text-slate-400">系统状态 <span v-if="status" class="text-slate-600">（采样于 {{ formatTime(status.sampledAt) }}）</span></h2>
        <div class="ml-auto flex items-center gap-2">
          <label class="flex items-center gap-1.5 text-xs text-slate-400">
            <input type="checkbox" v-model="autoRefresh" class="accent-cyan-400" /> 10s 自动刷新
          </label>
          <button class="btn btn-xs" :disabled="loading" @click="load()">{{ loading ? '采集中…' : '↻ 立即刷新' }}</button>
        </div>
      </div>

      <p v-if="!status && loading" class="text-slate-600 text-sm panel p-8 text-center">采集系统状态中…</p>

      <template v-if="status">
        <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
          <div class="panel p-4">
            <div class="text-xs text-slate-500 mb-1">CPU 使用率</div>
            <div class="font-display text-2xl" :class="usageColor(status.cpu.usagePercent).text">{{ status.cpu.usagePercent.toFixed(1) }}%</div>
            <div class="h-1.5 bg-cyber-line rounded mt-2 overflow-hidden">
              <div class="h-full rounded transition-all" :class="usageColor(status.cpu.usagePercent).bar" :style="{ width: status.cpu.usagePercent + '%' }"></div>
            </div>
          </div>
          <div class="panel p-4">
            <div class="text-xs text-slate-500 mb-1">内存使用率</div>
            <div class="font-display text-2xl" :class="usageColor(status.memory.usagePercent).text">{{ status.memory.usagePercent.toFixed(1) }}%</div>
            <div class="text-xs text-slate-500 mt-1">{{ formatBytes(status.memory.usedBytes) }} / {{ formatBytes(status.memory.totalBytes) }}</div>
          </div>
          <div class="panel p-4">
            <div class="text-xs text-slate-500 mb-1">根分区使用率</div>
            <div class="font-display text-2xl" :class="usageColor(status.disks.length ? status.disks[0].usagePercent : 0).text">
              {{ status.disks.length ? status.disks[0].usagePercent.toFixed(1) + '%' : '—' }}
            </div>
            <div class="text-xs text-slate-500 mt-1">{{ status.disks.length ? status.disks[0].mount : '—' }}</div>
          </div>
          <div class="panel p-4">
            <div class="text-xs text-slate-500 mb-1">运行时长</div>
            <div class="font-display text-2xl text-cyan-300">{{ formatUptime(status.host.uptimeSeconds) }}</div>
          </div>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <div class="panel p-4 flex flex-col gap-2">
            <h3 class="text-sm text-slate-300 font-medium">CPU</h3>
            <div class="text-xs text-slate-500 font-mono truncate" :title="status.cpu.modelName">{{ status.cpu.modelName }}</div>
            <div class="flex gap-4 text-xs text-slate-400">
              <span>核心 <span class="text-slate-200">{{ status.cpu.coreCount }}</span></span>
              <span>负载 <span class="text-slate-200 font-mono">{{ status.cpu.load1.toFixed(2) }} / {{ status.cpu.load5.toFixed(2) }} / {{ status.cpu.load15.toFixed(2) }}</span></span>
            </div>
            <div class="mt-auto pt-2">
              <div class="flex justify-between text-xs text-slate-500 mb-1"><span>内存 / Swap</span>
                <span>{{ formatBytes(status.memory.usedBytes) }} · Swap {{ formatBytes(status.memory.swapUsedBytes) }}/{{ formatBytes(status.memory.swapTotalBytes) }}</span></div>
              <div class="h-1.5 bg-cyber-line rounded overflow-hidden">
                <div class="h-full rounded bg-violet-400" :style="{ width: status.memory.usagePercent + '%' }"></div>
              </div>
            </div>
          </div>

          <div class="panel p-4">
            <h3 class="text-sm text-slate-300 font-medium mb-2">主机信息</h3>
            <div class="grid grid-cols-[5rem_1fr] gap-x-3 gap-y-1.5 text-xs">
              <span class="text-slate-500">主机名</span><span class="text-slate-200 truncate">{{ status.host.hostName }}</span>
              <span class="text-slate-500">系统</span><span class="text-slate-200 truncate" :title="status.host.osName">{{ status.host.osName }}</span>
              <span class="text-slate-500">内核</span><span class="text-slate-200 font-mono truncate">{{ status.host.kernelVersion }}</span>
              <span class="text-slate-500">架构</span><span class="text-slate-200">{{ status.host.architecture }}</span>
            </div>
            <div v-if="status.hardware && status.hardware.length" class="mt-3 border-t border-cyber-line/40 pt-3">
              <div class="text-xs text-slate-500 mb-2" style="display:flex;align-items:center;gap:0.4rem">
                硬件设备
                <span class="badge border-rose-500/50 text-rose-300">{{ status.hardware.filter(h => h.isGpu).length }} GPU</span>
                <span class="badge border-slate-500/40 text-slate-400">{{ status.hardware.length }} 项</span>
              </div>
              <div class="flex flex-col gap-1 max-h-56 overflow-y-auto pr-1">
                <div v-for="h in status.hardware" :key="h.type + h.name"
                     class="flex items-center gap-2 text-xs py-0.5 border-b border-cyber-line/20"
                     :class="h.isGpu ? 'text-rose-300 bg-rose-500/5 rounded px-1' : 'text-slate-400'">
                  <span class="font-mono truncate" :title="h.name">{{ h.isGpu ? '🖥' : (h.type === 'usb' ? '🔌' : '🧩') }}</span>
                  <span class="font-mono truncate" :title="h.description">{{ h.name }}</span>
                  <span class="ml-auto text-[11px] truncate max-w-[45%]" :title="h.description">
                    <span v-if="h.isGpu" class="text-rose-300">{{ h.description }}</span>
                    <template v-else>{{ h.description }}</template>
                  </span>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <div class="panel p-4">
            <h3 class="text-sm text-slate-300 font-medium mb-3">磁盘挂载点</h3>
            <div class="flex flex-col gap-3">
              <p v-if="!status.disks.length" class="text-slate-600 text-sm">未采集到磁盘信息</p>
              <div v-for="disk in status.disks" :key="disk.mount">
                <div class="flex justify-between text-xs mb-1">
                  <span class="font-mono text-cyan-300/80">{{ disk.mount }} <span class="text-slate-600">({{ disk.fileSystem }})</span></span>
                  <span :class="usageColor(disk.usagePercent).text">{{ formatBytes(disk.usedBytes) }} / {{ formatBytes(disk.totalBytes) }} · {{ disk.usagePercent }}%</span>
                </div>
                <div class="h-1.5 bg-cyber-line rounded overflow-hidden">
                  <div class="h-full rounded transition-all" :class="usageColor(disk.usagePercent).bar" :style="{ width: disk.usagePercent + '%' }"></div>
                </div>
              </div>
            </div>
          </div>

          <div class="panel p-4">
            <h3 class="text-sm text-slate-300 font-medium mb-3">网卡速率</h3>
            <div class="flex flex-col gap-2">
              <p v-if="!status.networks.length" class="text-slate-600 text-sm">当前环境不采集网络数据</p>
              <div v-for="net in status.networks" :key="net.name"
                   class="flex items-center gap-3 text-xs border border-cyber-line/60 rounded-lg px-3 py-2">
                <span class="font-mono text-cyan-300/80 w-24 truncate">{{ net.name }}</span>
                <span class="text-emerald-300">↓ {{ formatBps(net.recvBytesPerSec) }}</span>
                <span class="text-amber-300">↑ {{ formatBps(net.sentBytesPerSec) }}</span>
                <span class="ml-auto text-slate-600">累计 ↓{{ formatBytes(net.totalRecvBytes) }} ↑{{ formatBytes(net.totalSentBytes) }}</span>
              </div>
            </div>
          </div>
        </div>

        <div class="panel p-4">
          <div class="flex items-center gap-2 mb-2 flex-wrap">
            <h3 class="text-sm text-slate-300 font-medium">进程 TOP <span class="text-xs text-slate-500 font-normal">（即时）</span></h3>
            <div class="ml-auto flex gap-1.5 flex-wrap">
              <button v-for="s in PROCESS_SORTS" :key="s.value" class="btn btn-xs"
                      :class="overviewProcessSort === s.value ? 'btn-primary' : ''"
                      @click="overviewProcessSort = s.value">{{ s.label }}</button>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <div v-for="p in overviewProcesses(status, overviewProcessSort)" :key="'o' + p.pid"
                 class="py-1 border-b border-cyber-line/40">
              <div class="flex justify-between text-xs">
                <span class="text-slate-300 truncate">{{ p.name }} <span class="text-slate-600">({{ p.pid }})</span></span>
                <span class="font-mono text-cyan-300">{{ p.cpuPercent.toFixed(1) }}% · {{ formatBytes(p.memBytes) }} · {{ p.memPercent.toFixed(1) }}%</span>
              </div>
              <div v-if="p.diskReadBps || p.diskWriteBps || p.netSentBps || p.netRecvBps"
                   class="flex justify-between text-[11px] text-slate-500 mt-0.5">
                <span v-if="p.diskReadBps || p.diskWriteBps" class="truncate">
                  磁盘 <span class="text-emerald-300/90">↓{{ formatBps(p.diskReadBps) }}</span> <span class="text-amber-300/90">↑{{ formatBps(p.diskWriteBps) }}</span>
                </span>
                <span v-if="p.netSentBps || p.netRecvBps" class="font-mono">
                  网络 <span class="text-emerald-300/90">↓{{ formatBps(p.netRecvBps) }}</span> <span class="text-amber-300/90">↑{{ formatBps(p.netSentBps) }}</span>
                </span>
              </div>
            </div>
            <p v-if="!overviewProcesses(status, overviewProcessSort).length" class="text-slate-600 text-xs">无数据</p>
          </div>
        </div>

        <div class="panel p-4">
          <div class="flex items-center gap-2 mb-2 flex-wrap">
            <h3 class="text-sm text-slate-300 font-medium">历史趋势</h3>
            <div class="ml-auto flex gap-1.5">
              <button v-for="range in RANGES" :key="range.hours" class="btn btn-xs"
                      :class="historyRange === range.hours ? 'btn-primary' : ''"
                      @click="setRange(range.hours)">{{ range.label }}</button>
            </div>
          </div>
          <div class="text-xs text-slate-500 mb-1">CPU / 内存 / 根分区</div>
          <div ref="usageChartEl" class="w-full"></div>
          <div class="text-xs text-slate-500 mb-1 mt-3">网卡速率（KB/s）</div>
          <div ref="netChartEl" class="w-full mt-1"></div>
          <div class="text-xs text-slate-500 mb-1 mt-3">磁盘挂载点使用率（%）</div>
          <div ref="diskChartEl" class="w-full mt-1"></div>
          <p class="text-xs text-slate-600 mt-2">整机 60s 采样；基线 30 分钟，异常（CPU≥80% 或 内存≥85%）时 10 分钟；保留 7 天。</p>
        </div>

        <div class="panel p-4">
          <div class="flex items-center gap-2 mb-2 flex-wrap">
            <h3 class="text-sm text-slate-300 font-medium">进程占用采样点</h3>
            <div class="ml-auto flex gap-1.5 flex-wrap">
              <button v-for="s in PROCESS_SORTS" :key="s.value" class="btn btn-xs"
                      :class="processSort === s.value ? 'btn-primary' : ''"
                      @click="processSort = s.value">{{ s.label }}</button>
            </div>
          </div>
          <p class="text-xs text-slate-600 mb-3">点击某个采样点，查看当时 Top 进程、磁盘挂载点与网卡速率；CPU / 内存峰值可与上方趋势对应。</p>
          <div class="flex flex-col gap-1.5 max-h-72 overflow-y-auto">
            <p v-if="!processPoints.length" class="text-slate-600 text-xs">暂无进程采样数据</p>
            <button v-for="p in processPoints" :key="new Date(p.time).getTime()"
                    class="flex items-center justify-between text-xs border border-cyber-line/60 rounded-lg px-3 py-2 hover:border-cyan-400/60 transition-colors"
                    :class="selectedPoint && new Date(selectedPoint.time).getTime() === new Date(p.time).getTime() ? 'border-cyan-400' : ''"
                    @click="selectPoint(p)">
              <span class="font-mono text-slate-300">{{ formatTime(p.time) }}</span>
              <span class="text-slate-500">{{ p.processes.length }} 个进程</span>
            </button>
          </div>
          <div v-if="selectedPoint" class="mt-3 border-t border-cyber-line/40 pt-3">
            <div class="text-xs text-slate-500 mb-2">采样点 {{ formatTime(selectedPoint.time) }} 的资源概况</div>

            <div v-if="selectedPoint.system" class="flex gap-4 text-xs text-slate-400 mb-2">
              <span>整机 <span class="text-cyan-300 font-mono">CPU {{ selectedPoint.system.cpuUsage.toFixed(1) }}%</span></span>
              <span>内存 <span class="text-violet-300 font-mono">{{ selectedPoint.system.memUsage.toFixed(1) }}%</span></span>
              <span v-if="selectedPoint.system.netRecvBps || selectedPoint.system.netSentBps" class="font-mono">
                全网卡 <span class="text-emerald-300">↓{{ formatBps(selectedPoint.system.netRecvBps) }}</span>
                <span class="text-amber-300">↑{{ formatBps(selectedPoint.system.netSentBps) }}</span>
              </span>
            </div>

            <div v-if="selectedPoint.disks.length" class="mb-2">
              <div class="text-[11px] text-slate-500 mb-1">磁盘挂载点</div>
              <div class="flex flex-col gap-0.5">
                <div v-for="d in selectedPoint.disks" :key="d.mount" class="flex justify-between text-[11px] text-slate-400">
                  <span class="font-mono text-cyan-300/80 truncate">{{ d.mount }}</span>
                  <span class="font-mono">{{ d.usagePercent.toFixed(1) }}% · {{ formatBytes(d.usedBytes) }}/{{ formatBytes(d.totalBytes) }}</span>
                </div>
              </div>
            </div>

            <div v-if="selectedPoint.networks.length" class="mb-2">
              <div class="text-[11px] text-slate-500 mb-1">网卡速率</div>
              <div class="flex flex-col gap-0.5">
                <div v-for="n in selectedPoint.networks" :key="n.name" class="flex justify-between text-[11px] text-slate-400">
                  <span class="font-mono text-cyan-300/80 truncate">{{ n.name }}</span>
                  <span class="font-mono"><span class="text-emerald-300">↓{{ formatBps(n.recvBytesPerSec) }}</span> <span class="text-amber-300">↑{{ formatBps(n.sentBytesPerSec) }}</span></span>
                </div>
              </div>
            </div>

            <div class="text-xs text-slate-500 mb-2">当时的 Top 进程</div>
            <div class="flex flex-col gap-1">
              <div v-for="proc in sortedProcesses(selectedPoint.processes, processSort)" :key="proc.pid"
                   class="py-1 border-b border-cyber-line/40">
                <div class="flex justify-between text-xs">
                  <span class="text-slate-300 truncate">{{ proc.name }} <span class="text-slate-600">({{ proc.pid }})</span></span>
                  <span class="font-mono text-cyan-300">{{ proc.cpuPercent.toFixed(1) }}% · {{ formatBytes(proc.memBytes) }} · {{ proc.memPercent.toFixed(1) }}%</span>
                </div>
                <div v-if="proc.diskReadBps || proc.diskWriteBps || proc.netSentBps || proc.netRecvBps"
                     class="flex justify-between text-[11px] text-slate-500 mt-0.5">
                  <span v-if="proc.diskReadBps || proc.diskWriteBps" class="truncate">
                    磁盘 <span class="text-emerald-300/90">↓{{ formatBps(proc.diskReadBps) }}</span> <span class="text-amber-300/90">↑{{ formatBps(proc.diskWriteBps) }}</span>
                  </span>
                  <span v-if="proc.netSentBps || proc.netRecvBps" class="font-mono">
                    网络 <span class="text-emerald-300/90">↓{{ formatBps(proc.netRecvBps) }}</span> <span class="text-amber-300/90">↑{{ formatBps(proc.netSentBps) }}</span>
                  </span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </template>
    </div>
  `,
});
