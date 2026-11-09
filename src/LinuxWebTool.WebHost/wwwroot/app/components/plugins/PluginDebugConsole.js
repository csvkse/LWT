import { computed, defineComponent } from 'vue';

export default defineComponent({
  name: 'PluginDebugConsole',
  props: {
    result: {
      type: Object,
      default: null,
    },
    loading: {
      type: Boolean,
      default: false,
    },
    loadingText: {
      type: String,
      default: '运行中...',
    },
  },
  emits: ['clear'],
  setup(props) {
    function stripAnsi(text) {
      if (!text) return '';
      return String(text)
        .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)/g, '')
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
        .replace(/\]133;[A-Za-z0-9;=._-]*/g, '')
        .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, '')
        .trim();
    }

    const cleanOutput = computed(() => stripAnsi(props.result?.output || ''));
    const cleanError = computed(() => stripAnsi(props.result?.error || ''));

    return {
      cleanOutput,
      cleanError,
    };
  },
  template: `
    <div class="h-44 border-t border-cyber-line/60 bg-slate-950 flex flex-col shrink-0">
      <div class="px-3 py-1 bg-slate-900 border-b border-cyber-line/40 text-xs flex items-center justify-between text-slate-300">
        <span class="font-semibold flex items-center gap-1.5">
          <span>📟 调试与构建控制台</span>
          <span v-if="result" class="text-[11px] font-mono text-slate-400">
            退出码: {{ result.exitCode ?? 0 }} | 耗时: {{ Math.round(result.durationMs || result.elapsedMs || 0) }}ms
          </span>
        </span>
        <button type="button" class="btn-ghost text-[11px] p-0.5 text-slate-400 hover:text-slate-200" @click="$emit('clear')">
          清除
        </button>
      </div>
      <div class="flex-1 p-2.5 overflow-y-auto font-mono text-xs space-y-1">
        <div v-if="loading" class="text-amber-400 animate-pulse">{{ loadingText }}</div>
        <div v-if="cleanOutput" class="text-slate-300 whitespace-pre-wrap">{{ cleanOutput }}</div>
        <div v-if="cleanError" class="text-red-400 whitespace-pre-wrap">{{ cleanError }}</div>
        <div v-if="!loading && !cleanOutput && !cleanError" class="text-slate-500 italic">
          （无标准输出或错误信息）
        </div>
      </div>
    </div>
  `,
});
