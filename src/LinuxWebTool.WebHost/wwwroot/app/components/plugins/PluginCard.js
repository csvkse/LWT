import { computed, defineComponent } from 'vue';

const RUNTIME_MAP = {
  0: { label: 'Node.js', icon: '🟢', color: 'text-emerald-400 border-emerald-500/30 bg-emerald-500/10' },
  1: { label: 'Python', icon: '🐍', color: 'text-amber-400 border-amber-500/30 bg-amber-500/10' },
  2: { label: '.NET AOT', icon: '⚡', color: 'text-purple-400 border-purple-500/30 bg-purple-500/10' },
  3: { label: 'Bash', icon: '🐚', color: 'text-sky-400 border-sky-500/30 bg-sky-500/10' },
  4: { label: 'PowerShell', icon: '💻', color: 'text-blue-400 border-blue-500/30 bg-blue-500/10' },
  5: { label: 'Native', icon: '⚙️', color: 'text-slate-300 border-slate-500/30 bg-slate-500/10' },
};

const STATE_MAP = {
  0: { label: '已停止', color: 'bg-slate-700 text-slate-300' },
  1: { label: '启动中', color: 'bg-amber-500/20 text-amber-300 border border-amber-500/40 animate-pulse' },
  2: { label: '运行中', color: 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/40' },
  3: { label: '停止中', color: 'bg-orange-500/20 text-orange-300 border border-orange-500/40' },
  4: { label: '降级', color: 'bg-amber-700/30 text-amber-400 border border-amber-600/50' },
  5: { label: '异常', color: 'bg-red-500/20 text-red-300 border border-red-500/40' },
  6: { label: '已禁用', color: 'bg-slate-700 text-slate-400' },
  7: { label: '缺少依赖', color: 'bg-amber-500/20 text-amber-300 border border-amber-500/40' },
};

export default defineComponent({
  name: 'PluginCard',
  props: {
    plugin: {
      type: Object,
      required: true,
    },
  },
  emits: [
    'start',
    'stop',
    'restart',
    'action',
    'code',
    'logs',
    'kv',
    'webview',
    'delete',
    'toggle-enabled',
    'install-guide',
  ],
  setup(props, { emit }) {
    const isResident = computed(() => props.plugin.profile === 0);
    const isRunning = computed(() => props.plugin.state === 2);
    const isStarting = computed(() => props.plugin.state === 1);
    const isDegradedOrError = computed(() => props.plugin.state === 4 || props.plugin.state === 5);
    const isMissingRuntime = computed(() => Boolean(props.plugin.isMissingRuntime || props.plugin.state === 7));

    const runtimeMeta = computed(() => RUNTIME_MAP[props.plugin.runtime] || RUNTIME_MAP[5]);
    const stateMeta = computed(() => {
      if (isMissingRuntime.value) {
        return STATE_MAP[7];
      }
      if (!isResident.value) {
        return props.plugin.manifest?.enabled !== false
          ? { label: '已就绪', color: 'bg-sky-500/20 text-sky-300 border border-sky-500/40' }
          : STATE_MAP[6];
      }
      return STATE_MAP[props.plugin.state] || STATE_MAP[0];
    });

    const hasWebview = computed(() => Boolean(props.plugin.manifest?.ui?.hasWebview || props.plugin.manifest?.ui?.entry));
    const hasHttpService = computed(() => isResident.value && Boolean(props.plugin.manifest?.network?.supportHttp));
    const customActions = computed(() => props.plugin.manifest?.customActions || []);

    function handleStart() { emit('start', props.plugin); }
    function handleStop() { emit('stop', props.plugin); }
    function handleRestart() { emit('restart', props.plugin); }
    function handleAction(act) { emit('action', { plugin: props.plugin, action: act }); }
    function handleCode() { emit('code', props.plugin); }
    function handleLogs() { emit('logs', props.plugin); }
    function handleKv() { emit('kv', props.plugin); }
    function handleWebview() { emit('webview', props.plugin); }
    function handleDelete() { emit('delete', props.plugin); }
    function handleToggleEnabled() { emit('toggle-enabled', props.plugin); }
    function handleInstallGuide() { emit('install-guide', props.plugin); }

    function openGateway() {
      if (props.plugin.gatewayUrl) {
        window.open(props.plugin.gatewayUrl, '_blank');
      }
    }

    return {
      isResident,
      isRunning,
      isStarting,
      isDegradedOrError,
      isMissingRuntime,
      runtimeMeta,
      stateMeta,
      hasWebview,
      hasHttpService,
      customActions,
      handleStart,
      handleStop,
      handleRestart,
      handleAction,
      handleCode,
      handleLogs,
      handleKv,
      handleWebview,
      handleDelete,
      handleToggleEnabled,
      handleInstallGuide,
      openGateway,
    };
  },
  template: `
    <div class="panel p-4 flex flex-col justify-between h-full border border-cyber-line/50 hover:border-cyan-500/40 transition-colors">
      <!-- 头部：图标、名称、双徽章与启停开关 -->
      <div>
        <div class="flex items-start justify-between gap-3">
          <div class="flex items-center gap-2.5 min-w-0">
            <div class="w-10 h-10 rounded-lg flex items-center justify-center text-xl shrink-0 border"
              :class="isResident ? 'bg-amber-500/10 border-amber-500/30 text-amber-300' : 'bg-cyan-500/10 border-cyan-500/30 text-cyan-300'">
              {{ isResident ? '⚡' : '🔌' }}
            </div>
            <div class="min-w-0">
              <div class="flex items-center gap-1.5 flex-wrap">
                <span class="font-display font-semibold text-slate-100 text-sm truncate" :title="plugin.name">
                  {{ plugin.name }}
                </span>
                <span class="text-[10px] text-slate-400 font-mono">v{{ plugin.manifest?.version || '1.0.0' }}</span>
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate" :title="plugin.id">
                {{ plugin.id }}
              </div>
            </div>
          </div>

          <!-- Profile & State 状态徽章 -->
          <div class="flex flex-col items-end gap-1 shrink-0">
            <span class="text-[10px] px-2 py-0.5 rounded-full font-medium" :class="stateMeta.color">
              {{ stateMeta.label }}
            </span>
            <span class="text-[10px] px-1.5 py-0.5 rounded border" :class="runtimeMeta.color">
              {{ runtimeMeta.icon }} {{ runtimeMeta.label }}
            </span>
          </div>
        </div>

        <!-- 描述与分类 -->
        <p class="text-xs text-slate-300 mt-2.5 line-clamp-2 min-h-[2rem]" :title="plugin.manifest?.description">
          {{ plugin.manifest?.description || '暂无描述' }}
        </p>

        <!-- 元数据指标：端口、PID、崩溃次数、分类 -->
        <div class="mt-3 pt-2 border-t border-cyber-line/40 grid grid-cols-2 gap-2 text-[11px]">
          <div class="text-slate-400 truncate">
            类型:
            <span class="text-slate-200 font-medium">
              {{ isResident ? '常驻微服务' : '一次性脚本' }}
            </span>
          </div>
          <div class="text-slate-400 truncate">
            分类:
            <span class="text-slate-200">{{ plugin.manifest?.category || '默认' }}</span>
          </div>
          <div v-if="isResident" class="text-slate-400 truncate">
            端口:
            <span v-if="plugin.allocatedPort" class="text-cyan-300 font-mono font-medium">
              :{{ plugin.allocatedPort }}
            </span>
            <span v-else class="text-slate-500">未分配</span>
          </div>
          <div v-if="isResident" class="text-slate-400 truncate">
            PID:
            <span v-if="plugin.processId" class="text-slate-200 font-mono">
              {{ plugin.processId }}
            </span>
            <span v-else class="text-slate-500">-</span>
          </div>
        </div>

        <!-- 异常提示 -->
        <div v-if="plugin.lastError" class="mt-2 text-[11px] text-red-400 bg-red-950/30 border border-red-800/40 rounded px-2 py-1 line-clamp-2" :title="plugin.lastError">
          ⚠️ {{ plugin.lastError }}
        </div>

        <!-- 缺失依赖引导横幅 -->
        <div v-if="isMissingRuntime" class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 flex items-center justify-between gap-2">
          <div class="text-[11px] text-amber-300 font-mono truncate" :title="plugin.missingRuntimeMessage || '缺少运行环境或必要依赖'">
            ⚠️ {{ plugin.missingRuntimeMessage || '缺少运行环境或依赖' }}
          </div>
          <button
            type="button"
            class="text-[11px] text-cyan-400 hover:text-cyan-300 font-medium shrink-0 underline flex items-center gap-1"
            @click="handleInstallGuide()"
          >
            <span>🛠️</span>
            <span>安装引导</span>
          </button>
        </div>

        <!-- 三种 UI 模式触发区 -->
        <div class="mt-3 space-y-2">
          <!-- 模式 3: 嵌入式 Webview 弹窗入口 -->
          <button
            v-if="hasWebview"
            type="button"
            class="btn-primary w-full py-1.5 text-xs flex items-center justify-center gap-1.5 bg-gradient-to-r from-cyan-600 to-blue-600 hover:from-cyan-500 hover:to-blue-500 text-white font-medium shadow-sm"
            @click="handleWebview()"
          >
            <span>🖥️</span>
            <span>打开 Webview 扩展视图</span>
          </button>

          <!-- 模式 2: 独立 HTTP Gateway 直达 -->
          <button
            v-if="hasHttpService && isRunning"
            type="button"
            class="btn-secondary w-full py-1.5 text-xs flex items-center justify-center gap-1.5 text-cyan-300 hover:bg-cyan-500/10 border-cyan-500/30"
            @click="openGateway()"
          >
            <span>🌐</span>
            <span>访问微服务网页 / 接口</span>
            <span class="text-[10px]">↗</span>
          </button>

          <!-- 模式 1: 自定义契约动作按钮 -->
          <div v-if="customActions.length > 0" class="flex flex-wrap gap-1.5">
            <button
              v-for="act in customActions"
              :key="act.action || act.name"
              type="button"
              class="btn-ghost py-1 px-2 text-[11px] rounded border border-cyber-line hover:border-cyan-500/40 text-slate-200 flex items-center gap-1"
              @click="handleAction(act)"
            >
              <span>{{ act.icon || '⚡' }}</span>
              <span>{{ act.label || act.name || act.action }}</span>
            </button>
          </div>
        </div>
      </div>

      <!-- 底部操作栏：启停、日志、代码、KV、删除 -->
      <div class="mt-4 pt-3 border-t border-cyber-line/50 flex items-center justify-between gap-1 text-xs">
        <!-- 常驻生命周期控制 -->
        <div v-if="isResident" class="flex items-center gap-1">
          <button
            v-if="!isRunning"
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-emerald-400 hover:bg-emerald-500/10 rounded flex items-center gap-1"
            :disabled="isStarting"
            @click="handleStart()"
          >
            <span>▶</span>
            <span>启动</span>
          </button>
          <button
            v-if="isRunning"
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-orange-400 hover:bg-orange-500/10 rounded flex items-center gap-1"
            @click="handleStop()"
          >
            <span>⏹</span>
            <span>停止</span>
          </button>
          <button
            v-if="isRunning"
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-cyan-400 hover:bg-cyan-500/10 rounded flex items-center gap-1"
            @click="handleRestart()"
          >
            <span>🔄</span>
            <span>重启</span>
          </button>
        </div>

        <!-- 脚本类快速执行与启用开关 -->
        <div v-else class="flex items-center gap-1">
          <button
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-slate-300 hover:bg-slate-800 rounded flex items-center gap-1"
            @click="handleToggleEnabled()"
          >
            <span>{{ plugin.manifest?.enabled !== false ? '🟢 已启用' : '⚪ 已禁用' }}</span>
          </button>
        </div>

        <!-- 工具抽屉与治理 -->
        <div class="flex items-center gap-1 ml-auto">
          <button
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-amber-300 rounded"
            title="环境依赖与安装指引"
            @click="handleInstallGuide()"
          >
            <span>🛠️</span>
          </button>
          <button
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-cyan-300 rounded"
            title="查看实时运行日志"
            @click="handleLogs()"
          >
            <span>📜</span>
          </button>
          <button
            v-if="plugin.manifest?.storage?.enableKv"
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-amber-300 rounded"
            title="KV 存储空间"
            @click="handleKv()"
          >
            <span>🗄️</span>
          </button>
          <button
            v-if="plugin.runtime !== 5"
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-blue-300 rounded"
            title="在线编辑代码与调试"
            @click="handleCode()"
          >
            <span>✏️</span>
          </button>
          <button
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-red-400 rounded"
            title="卸载插件"
            @click="handleDelete()"
          >
            <span>🗑️</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
