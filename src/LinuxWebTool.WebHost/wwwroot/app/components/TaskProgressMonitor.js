import { computed, defineComponent } from 'vue';

// 任务实时执行进度面板组件（适配器模式 + 赛博主题微仪表盘）
export default defineComponent({
  name: 'TaskProgressMonitor',
  props: {
    progress: { type: Object, default: () => ({}) },
    variant: { type: String, default: 'subrow' }, // 'subrow' | 'card' | 'inline'
  },
  setup(props) {
    const percent = computed(() => {
      const p = props.progress?.filePercent ?? 0;
      return Math.min(100, Math.max(0, p));
    });

    const isCancelling = computed(() => {
      const desc = props.progress?.phaseDescription || '';
      return desc.includes('终止') || desc.includes('取消') || desc.includes('Cancelling');
    });

    function formatSpeed(bytesPerSec) {
      if (!bytesPerSec || bytesPerSec <= 0) return '0 B/s';
      if (bytesPerSec >= 1024 * 1024 * 1024) return (bytesPerSec / (1024 * 1024 * 1024)).toFixed(1) + ' GB/s';
      if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / (1024 * 1024)).toFixed(1) + ' MB/s';
      if (bytesPerSec >= 1024) return (bytesPerSec / 1024).toFixed(1) + ' KB/s';
      return Math.round(bytesPerSec) + ' B/s';
    }

    function formatBytes(bytes) {
      if (!bytes || bytes <= 0) return '0 B';
      if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
      if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
      if (bytes >= 1024) return (bytes / 1024).toFixed(1) + ' KB';
      return bytes + ' B';
    }

    function formatEta(remainingMs) {
      if (!remainingMs || remainingMs <= 0) return '';
      const seconds = Math.round(remainingMs / 1000);
      if (seconds < 60) return `${seconds}秒`;
      const minutes = Math.floor(seconds / 60);
      const remSec = seconds % 60;
      if (minutes < 60) return `${minutes}分${remSec}秒`;
      const hours = Math.floor(minutes / 60);
      return `${hours}小时${minutes % 60}分`;
    }

    return {
      percent,
      isCancelling,
      formatSpeed,
      formatBytes,
      formatEta,
    };
  },
  template: `
    <div
      class="rounded-lg border text-xs shadow-inner transition-all duration-300"
      :class="isCancelling
        ? 'bg-rose-950/30 border-rose-500/40 text-rose-200'
        : 'bg-cyan-950/40 border-cyan-500/30 text-slate-200'"
      :style="variant === 'subrow' ? 'padding: 0.75rem 1rem;' : 'padding: 0.625rem;'"
    >
      <!-- 头部：阶段描述与大字号百分比 -->
      <div class="flex items-center justify-between gap-3 mb-2">
        <div class="flex items-center gap-2 min-w-0 flex-1">
          <span
            class="inline-block w-2.5 h-2.5 rounded-full shrink-0"
            :class="isCancelling ? 'bg-rose-400 animate-ping' : 'bg-cyan-400 animate-pulse'"
          ></span>
          <span
            class="font-medium truncate text-xs"
            :class="isCancelling ? 'text-rose-300 font-semibold' : 'text-cyan-300'"
            :title="progress?.phaseDescription || '正在处理中...'"
          >
            {{ progress?.phaseDescription || '正在处理中...' }}
          </span>
        </div>
        <div
          class="font-mono font-bold text-sm shrink-0 tracking-tight"
          :class="isCancelling ? 'text-rose-400' : 'text-cyan-400'"
        >
          {{ percent.toFixed(1) }}%
        </div>
      </div>

      <!-- 动态渐变进度条（双色流光效果） -->
      <div class="w-full bg-slate-900/90 rounded-full h-2 overflow-hidden mb-2 border border-cyber-line/50 p-0.5">
        <div
          class="h-full rounded-full transition-all duration-300 ease-out"
          :class="isCancelling
            ? 'bg-gradient-to-r from-amber-500 to-rose-500'
            : 'bg-gradient-to-r from-cyan-500 via-teal-400 to-emerald-400 shadow-[0_0_8px_rgba(34,211,238,0.5)]'"
          :style="{ width: Math.max(2, percent) + '%' }"
        ></div>
      </div>

      <!-- 核心指标网格：处理项目、传输体积、速率与剩余时间 -->
      <div class="flex flex-wrap items-center justify-between gap-y-1 gap-x-3 text-[11px] text-slate-400">
        <div class="flex items-center gap-1.5 font-mono">
          <span class="text-slate-300 font-medium">
            {{ (progress?.processedFiles || 0).toLocaleString() }} / {{ Math.max(progress?.processedFiles || 0, progress?.scannedFiles || 0).toLocaleString() }} 项
          </span>
          <span class="text-slate-600">·</span>
          <span>{{ formatBytes(progress?.processedBytes || 0) }}</span>
        </div>
        <div class="flex items-center gap-2.5 font-mono ml-auto">
          <span v-if="progress?.speedBytesPerSec > 0" class="text-cyan-300 flex items-center gap-0.5">
            <span>⚡</span>
            <span>{{ formatSpeed(progress?.speedBytesPerSec) }}</span>
          </span>
          <span v-if="progress?.estimatedRemainingMs > 0" class="text-amber-300 flex items-center gap-0.5">
            <span>⏳</span>
            <span>约 {{ formatEta(progress?.estimatedRemainingMs) }}</span>
          </span>
        </div>
      </div>

      <!-- 当前正在处理的文件路径（安全单行截断展示） -->
      <div
        v-if="progress?.currentItemPath"
        class="text-[10px] text-slate-400 truncate mt-1.5 font-mono bg-black/30 px-2 py-0.5 rounded border border-cyber-line/30"
        :title="progress?.currentItemPath"
      >
        <span class="text-slate-500 mr-1">处理中:</span>
        <span class="text-slate-300">{{ progress?.currentItemPath }}</span>
      </div>
    </div>
  `,
});
