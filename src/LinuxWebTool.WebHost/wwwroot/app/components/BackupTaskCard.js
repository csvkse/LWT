import { computed, defineComponent } from 'vue';
import TaskProgressMonitor from './TaskProgressMonitor.js';

// 移动端专用备份与同步任务赛博卡片组件（响应式触控友好布局）
export default defineComponent({
  name: 'BackupTaskCard',
  components: { TaskProgressMonitor },
  props: {
    task: { type: Object, required: true },
    progress: { type: Object, default: () => ({}) },
    mode: { type: String, default: 'backup' }, // 'backup' | 'sync'
  },
  emits: ['run', 'cancel', 'resume', 'snapshots', 'records', 'edit', 'delete', 'jump'],
  setup(props, { emit }) {
    const isRunning = computed(() => {
      return props.task.lastRunStatus === 'Running' || Boolean(props.progress?.isRunning);
    });

    const isAutoPaused = computed(() => {
      return props.task.lastRunStatus === 'AutoPaused';
    });

    function formatTime(t) {
      if (!t) return '';
      const d = new Date(t);
      const pad = (x) => String(x).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    function onRun() {
      emit('run', props.task);
    }

    function onCancel() {
      emit('cancel', props.task);
    }

    function onResume() {
      emit('resume', props.task);
    }

    function onSnapshots() {
      emit('snapshots', props.task);
    }

    function onRecords() {
      emit('records', props.task.id);
    }

    function onEdit() {
      emit('edit', props.task);
    }

    function onDelete() {
      emit('delete', props.task);
    }

    function onJump(path) {
      if (path) emit('jump', path);
    }

    return {
      isRunning,
      isAutoPaused,
      formatTime,
      onRun,
      onCancel,
      onResume,
      onSnapshots,
      onRecords,
      onEdit,
      onDelete,
      onJump,
    };
  },
  template: `
    <div
      class="panel p-3.5 flex flex-col gap-2.5 transition-all"
      :class="isRunning ? 'border-cyan-500/50 shadow-[0_0_12px_rgba(34,211,238,0.12)]' : ''"
    >
      <!-- 卡片头部：任务标题、模式与状态徽标 -->
      <div class="flex items-start justify-between gap-2">
        <div class="flex items-center gap-2 min-w-0 flex-1">
          <span class="font-display font-semibold text-slate-100 text-sm truncate" :title="task.name">
            {{ task.name }}
          </span>
          <span v-if="!task.enabled" class="badge border-slate-600/60 text-slate-500 shrink-0">停用</span>
          <span class="badge border-cyan-500/30 text-cyan-300/80 text-[10px] shrink-0">
            {{ mode === 'sync' ? '直接镜像' : '增量快照' }}
          </span>
        </div>

        <!-- 状态标签 -->
        <div class="shrink-0">
          <span
            v-if="isRunning"
            class="badge border-cyan-500/50 bg-cyan-500/10 text-cyan-300 font-medium animate-pulse"
          >
            ⚡ 运行中 {{ progress?.filePercent ? '(' + progress.filePercent.toFixed(0) + '%)' : '' }}
          </span>
          <span
            v-else-if="isAutoPaused"
            class="badge border-amber-500/50 bg-amber-500/10 text-amber-300 font-medium"
            :title="task.autoPausedReason || task.lastRunMessage"
          >
            ⚠ 自动暂停
          </span>
          <span
            v-else
            class="badge"
            :class="task.lastRunStatus === 'Success'
              ? 'border-emerald-500/50 text-emerald-300'
              : (task.lastRunStatus === 'Failed' ? 'border-rose-500/50 text-rose-300' : 'border-slate-600/50 text-slate-400')"
          >
            {{ task.lastRunStatus === 'Success' ? '成功' : (task.lastRunStatus === 'Failed' ? '失败' : (task.lastRunStatus || '未运行')) }}
          </span>
        </div>
      </div>

      <!-- 路径信息：源端 -> 目标端 (支持直达文件管理器) -->
      <div class="bg-black/25 rounded p-2 text-xs font-mono space-y-1.5 border border-cyber-line/30">
        <div class="flex items-center justify-between gap-1.5 text-cyan-300">
          <div class="flex items-center gap-1.5 min-w-0 truncate" :title="task.sourcePath">
            <span class="text-slate-500 text-[11px] shrink-0">源端:</span>
            <span class="truncate">{{ task.sourcePath }}</span>
          </div>
          <button
            type="button"
            class="text-[11px] px-1.5 py-0.5 rounded border border-cyan-500/30 text-cyan-400 hover:bg-cyan-500/20 shrink-0 inline-flex items-center gap-1 transition-colors"
            title="在文件管理器中打开源端目录"
            @click.stop="onJump(task.sourcePath)"
          >
            📂 打开
          </button>
        </div>
        <div class="flex items-center justify-between gap-1.5 text-slate-400">
          <div class="flex items-center gap-1.5 min-w-0 truncate" :title="task.targetPath">
            <span class="text-slate-500 text-[11px] shrink-0">目标:</span>
            <span class="truncate">{{ task.targetPath }}</span>
          </div>
          <button
            type="button"
            class="text-[11px] px-1.5 py-0.5 rounded border border-slate-600/50 text-slate-300 hover:bg-slate-700/50 hover:text-white shrink-0 inline-flex items-center gap-1 transition-colors"
            title="在文件管理器中打开目标目录"
            @click.stop="onJump(task.targetPath)"
          >
            📂 打开
          </button>
        </div>
      </div>

      <!-- 调度与策略标签 -->
      <div class="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
        <span class="badge border-slate-700 bg-slate-900/60 font-mono text-slate-300 py-0.5">
          🕒 {{ task.cronDescription || task.cronExpression }}
        </span>
        <span v-if="mode === 'backup'" class="badge border-slate-700 bg-slate-900/60 text-slate-400 py-0.5">
          GFS: {{ task.keepDays }}天 · {{ task.keepWeeks }}周 · {{ task.keepMonths }}月
        </span>
        <span v-else class="badge border-slate-700 bg-slate-900/60 text-slate-400 py-0.5">
          {{ task.syncDirection === 1 ? '单向镜像' : '单向累加' }}
          {{ task.syncDirection === 1 && task.deleteHandling === 2 ? '(回收站 ' + task.trashRetentionDays + '天)' : '' }}
        </span>
        <span v-if="task.lastRunTime" class="text-slate-500 ml-auto">
          {{ formatTime(task.lastRunTime) }}
        </span>
      </div>

      <!-- 运行期实时进度面板 -->
      <TaskProgressMonitor
        v-if="isRunning"
        :progress="progress"
        variant="card"
      />

      <!-- 移动端触控友好操作按钮条 -->
      <div class="flex items-center gap-1.5 pt-1 border-t border-cyber-line/40">
        <!-- 主操作按钮 -->
        <button
          v-if="isAutoPaused"
          type="button"
          class="btn btn-xs border-amber-500/50 text-amber-300 hover:bg-amber-500/20 flex-1 min-h-[2rem]"
          @click="onResume()"
        >
          恢复调度
        </button>
        <button
          v-else-if="isRunning"
          type="button"
          class="btn btn-xs border-rose-500/50 text-rose-300 hover:bg-rose-500/20 flex-1 min-h-[2rem]"
          @click="onCancel()"
        >
          终止任务
        </button>
        <button
          v-else
          type="button"
          class="btn btn-xs btn-primary flex-1 min-h-[2rem]"
          @click="onRun()"
        >
          {{ mode === 'sync' ? '立即同步' : '立即备份' }}
        </button>

        <!-- 辅助操作按钮 -->
        <button
          v-if="mode === 'backup'"
          type="button"
          class="btn btn-xs min-h-[2rem] px-2.5"
          @click="onSnapshots()"
        >
          快照
        </button>
        <button
          type="button"
          class="btn btn-xs min-h-[2rem] px-2.5"
          @click="onRecords()"
        >
          记录
        </button>
        <button
          type="button"
          class="btn btn-xs min-h-[2rem] px-2.5"
          @click="onEdit()"
        >
          编辑
        </button>
        <button
          type="button"
          class="btn btn-xs btn-danger min-h-[2rem] px-2.5"
          @click="onDelete()"
        >
          删除
        </button>
      </div>
    </div>
  `,
});
