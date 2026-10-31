import { defineComponent } from 'vue';
import { formatBytes, formatTime } from '../../utils/format.js';

export default defineComponent({
  name: 'PluginOperationPanel',
  props: {
    operation: { type: Object, default: () => ({}) },
    isPolling: { type: Boolean, default: false },
    progress: { type: Number, default: null },
    progressMessage: { type: String, default: '' },
  },
  emits: ['cancel', 'download-artifact'],
  setup(props, { emit }) {
    function getStatusBadge(status) {
      switch (status) {
        case 'succeeded':
          return { label: '执行成功', class: 'border-emerald-500/50 bg-emerald-500/10 text-emerald-300' };
        case 'running':
          return { label: '正在运行', class: 'border-cyan-500/50 bg-cyan-500/10 text-cyan-300 animate-pulse' };
        case 'queued':
          return { label: '排队等待', class: 'border-amber-500/50 bg-amber-500/10 text-amber-300' };
        case 'timedOut':
          return { label: '执行超时', class: 'border-rose-500/50 bg-rose-500/10 text-rose-300' };
        case 'cancelled':
          return { label: '已取消', class: 'border-slate-500/50 bg-slate-500/10 text-slate-300' };
        case 'failed':
        default:
          return { label: '执行失败', class: 'border-rose-500/50 bg-rose-500/10 text-rose-300' };
      }
    }

    return {
      getStatusBadge,
      formatBytes,
      formatTime,
    };
  },
  template: `
    <div class="p-4 rounded-lg bg-slate-900/80 border border-cyber-line/50 space-y-3">
      <!-- 头部状态与操作 -->
      <div class="flex items-center justify-between text-xs">
        <div class="flex items-center gap-2">
          <span class="font-mono text-slate-400">操作: {{ operation?.operationId }}</span>
          <span
            v-if="operation?.status"
            class="badge text-[11px]"
            :class="getStatusBadge(operation.status).class"
          >
            {{ getStatusBadge(operation.status).label }}
          </span>
        </div>
        <div class="flex items-center gap-2">
          <span v-if="operation?.createdAtUtc" class="text-[10px] text-slate-500">
            {{ formatTime(operation.createdAtUtc) }}
          </span>
          <button
            v-if="['queued', 'running'].includes(operation?.status)"
            type="button"
            class="btn btn-xs border-rose-500/40 text-rose-300 hover:bg-rose-500/20"
            @click="$emit('cancel', operation?.operationId)"
          >
            取消
          </button>
        </div>
      </div>

      <!-- 进度指示器 -->
      <div v-if="['queued', 'running'].includes(operation?.status)" class="space-y-1.5">
        <div class="flex items-center justify-between text-[11px]">
          <span class="text-cyan-300 flex items-center gap-1.5">
            <span class="w-1.5 h-1.5 rounded-full bg-cyan-400 animate-ping"></span>
            {{ progressMessage || (operation.status === 'running' ? '正在执行...' : '排队中...') }}
          </span>
          <span v-if="progress != null" class="font-mono text-cyan-400 font-semibold">{{ progress }}%</span>
        </div>
        <div class="w-full bg-slate-800 rounded-full h-1.5 overflow-hidden">
          <div
            class="bg-cyan-400 h-1.5 transition-all duration-300"
            :style="{ width: (progress != null ? progress : 100) + '%' }"
            :class="{ 'animate-pulse': progress == null }"
          ></div>
        </div>
      </div>

      <!-- 结果/消息展示 -->
      <div v-if="operation?.result" class="space-y-2 pt-2 border-t border-cyber-line/30 text-xs">
        <div class="flex items-center justify-between">
          <span
            class="font-medium"
            :class="operation.result.status === 'succeeded' ? 'text-emerald-400' : 'text-rose-400'"
          >
            {{ operation.result.status === 'succeeded' ? '✅ 执行完成' : '❌ 执行异常' }}
          </span>
          <span v-if="operation.result.exitCode != null" class="text-[10px] font-mono text-slate-400">
            退出码: {{ operation.result.exitCode }}
          </span>
        </div>

        <div v-if="operation.result.error" class="text-rose-300 bg-rose-950/30 p-2 rounded border border-rose-500/30 font-mono text-[11px] whitespace-pre-wrap">
          {{ operation.result.error }}
        </div>

        <div v-if="operation.result.message && !operation.result.error" class="text-slate-300 text-xs">
          {{ operation.result.message }}
        </div>

        <pre
          v-if="operation.result.data"
          class="p-2.5 bg-cyber-bg/90 rounded border border-cyber-line/50 font-mono text-[11px] text-slate-300 max-h-48 overflow-y-auto whitespace-pre-wrap select-all"
        >{{ JSON.stringify(operation.result.data, null, 2) }}</pre>

        <!-- 产物列表 -->
        <div v-if="operation.result.artifactRefs && operation.result.artifactRefs.length" class="space-y-1 pt-1">
          <div class="text-[11px] text-cyan-300 font-medium">📦 产物下载：</div>
          <div class="flex flex-wrap gap-2">
            <button
              v-for="art in operation.result.artifactRefs"
              :key="art.artifactId"
              type="button"
              class="btn btn-xs border-cyan-500/40 text-cyan-300 hover:bg-cyan-500/20 flex items-center gap-1 font-mono text-[11px]"
              @click="$emit('download-artifact', operation.operationId, art.artifactId)"
            >
              <span>💾 {{ art.filename || art.artifactId }}</span>
              <span class="text-[10px] text-slate-400">({{ formatBytes(art.sizeBytes) }})</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
