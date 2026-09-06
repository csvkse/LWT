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
    let usageChart = null;
    let netChart = null;
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
      if (usageChart) {
        usageChart.destroy();
      }
      usageChart = new window.uPlot(options, data, el);
    }

    function renderNetChart(rows) {
      const el = netChartEl.value;
      if (!el || typeof window.uPlot === 'undefined') return;
      const times = toPoints(rows);
      const data = [
        times,
        rows.map((r) => r.netRecvBps / 1024),
        rows.map((r) => r.netSentBps / 1024),
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
      if (netChart) {
        netChart.destroy();
      }
      netChart = new window.uPlot(options, data, el);
    }

    async function loadHistory() {
      const result = await http(API.systemStatus.history, { params: { hours: historyRange.value } });
      if (result.ok) {
        await nextTick();
        renderUsageChart(result.data);
        renderNetChart(result.data);
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

    onMounted(async () => {
      await load();
      await loadHistory();
      toggleAutoRefresh(autoRefresh.value);
    });

    onUnmounted(() => {
      if (refreshTimer) window.clearInterval(refreshTimer);
      if (usageChart) usageChart.destroy();
      if (netChart) netChart.destroy();
    });

    return {
      status, loading, autoRefresh, historyRange, usageChartEl, netChartEl,
      load, loadHistory, setRange, RANGES,
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
          <h3 class="text-sm text-slate-300 font-medium mb-2">进程 TOP <span class="text-xs text-slate-500 font-normal">（按 CPU / 内存）</span></h3>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <div class="text-xs text-slate-500 mb-1">CPU 占用</div>
              <div v-for="p in status.topCpuProcesses" :key="'c' + p.pid" class="flex justify-between text-xs py-1 border-b border-cyber-line/40">
                <span class="text-slate-300 truncate">{{ p.name }} <span class="text-slate-600">({{ p.pid }})</span></span>
                <span class="text-cyan-300 font-mono">{{ p.cpuPercent.toFixed(1) }}%</span>
              </div>
              <p v-if="!status.topCpuProcesses.length" class="text-slate-600 text-xs">无数据</p>
            </div>
            <div>
              <div class="text-xs text-slate-500 mb-1">内存占用</div>
              <div v-for="p in status.topMemProcesses" :key="'m' + p.pid" class="flex justify-between text-xs py-1 border-b border-cyber-line/40">
                <span class="text-slate-300 truncate">{{ p.name }} <span class="text-slate-600">({{ p.pid }})</span></span>
                <span class="text-violet-300 font-mono">{{ formatBytes(p.memBytes) }} · {{ p.memPercent.toFixed(1) }}%</span>
              </div>
              <p v-if="!status.topMemProcesses.length" class="text-slate-600 text-xs">无数据</p>
            </div>
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
          <div ref="usageChartEl" class="w-full"></div>
          <div ref="netChartEl" class="w-full mt-3"></div>
          <p class="text-xs text-slate-600 mt-2">每 60 秒采样一次，保留 7 天；下载/上传单位 KB/s。</p>
        </div>
      </template>
    </div>
  `,
});
