import { defineComponent, onUnmounted, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginLogsDrawer',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
    plugin: {
      type: Object,
      default: null,
    },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const loading = ref(false);
    const logs = ref([]);
    const autoRefresh = ref(true);
    let timer = null;

    watch(
      () => [props.show, props.plugin],
      ([show, p]) => {
        if (show && p) {
          fetchLogs();
          startTimer();
        } else {
          stopTimer();
        }
      },
      { immediate: true }
    );

    function startTimer() {
      stopTimer();
      timer = setInterval(() => {
        if (autoRefresh.value) {
          fetchLogs(false);
        }
      }, 2000);
    }

    function stopTimer() {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    }

    onUnmounted(() => {
      stopTimer();
    });

    async function fetchLogs(showSpinner = true) {
      if (!props.plugin) return;
      if (showSpinner) loading.value = true;
      try {
        const res = await http(API.plugins.logs(props.plugin.id, 200));
        if (res.ok && Array.isArray(res.data)) {
          logs.value = res.data;
        }
      } catch (err) {
        if (showSpinner) toast.error('获取日志异常: ' + err.message);
      } finally {
        if (showSpinner) loading.value = false;
      }
    }

    function clearViewer() {
      logs.value = [];
    }

    function handleClose() {
      stopTimer();
      emit('close');
    }

    return {
      loading,
      logs,
      autoRefresh,
      fetchLogs,
      clearViewer,
      handleClose,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-end bg-slate-950/70 backdrop-blur-sm">
      <div class="panel w-full max-w-2xl h-full p-0 flex flex-col border-l border-cyan-500/40 rounded-none shadow-2xl">
        <!-- 抽屉头部 -->
        <div class="px-4 py-3 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between shrink-0">
          <div class="flex items-center gap-2 min-w-0">
            <span class="text-lg">📜</span>
            <div class="min-w-0">
              <div class="font-display font-semibold text-slate-100 text-sm truncate">
                运行日志 - {{ plugin.name }}
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate">
                ID: {{ plugin.id }} | 状态: {{ plugin.state === 2 ? '运行中' : '空闲' }}
              </div>
            </div>
          </div>

          <div class="flex items-center gap-2">
            <label class="flex items-center gap-1.5 text-xs text-slate-400 cursor-pointer select-none">
              <input
                v-model="autoRefresh"
                type="checkbox"
                class="rounded bg-slate-900 border-slate-700 text-cyan-500"
              />
              <span>自动刷新</span>
            </label>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white"
              title="清空日志面板"
              @click="clearViewer()"
            >
              清空
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white"
              @click="handleClose()"
            >
              ✕
            </button>
          </div>
        </div>

        <!-- 日志内容滚动区 -->
        <div class="flex-1 p-3 bg-slate-950 overflow-y-auto font-mono text-xs space-y-1 select-text">
          <div v-if="loading && logs.length === 0" class="text-slate-500 italic py-4 text-center">
            正在读取日志...
          </div>
          <div v-else-if="logs.length === 0" class="text-slate-600 italic py-4 text-center">
            暂无日志输出记录
          </div>
          <div
            v-for="(line, idx) in logs"
            :key="idx"
            class="text-slate-300 leading-relaxed break-all border-b border-slate-900/50 pb-0.5"
            :class="{
              'text-red-400': line.includes('[ERR]') || line.includes('Error') || line.includes('Exception'),
              'text-amber-300': line.includes('[WARN]') || line.includes('Warning'),
              'text-cyan-300': line.includes('[SYS]'),
            }"
          >
            {{ line }}
          </div>
        </div>

        <!-- 底部状态 -->
        <div class="px-4 py-2 bg-slate-900 border-t border-cyber-line/40 text-xs text-slate-400 flex items-center justify-between shrink-0">
          <span>共 {{ logs.length }} 行日志</span>
          <button
            type="button"
            class="btn-ghost text-xs text-cyan-400 hover:underline py-0"
            @click="fetchLogs(true)"
          >
            手动刷新
          </button>
        </div>
      </div>
    </div>
  `,
});
