import { computed, defineComponent } from 'vue';
import { PluginStateMachine } from '../../utils/pluginStateMachine.js';
import { PluginUrlResolver } from '../../utils/pluginUrlResolver.js';
import { auth } from '../../store/auth.js';
import { toast } from '../../store/toast.js';

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
    'switch-port',
  ],
  setup(props, { emit }) {
    const isResident = computed(() => props.plugin.profile === 0);
    const isRunning = computed(() => props.plugin.state === 2);
    const isStarting = computed(() => props.plugin.state === 1);
    const isDegradedOrError = computed(() => props.plugin.state === 3 || props.plugin.state === 4 || props.plugin.state === 5);
    const isMissingRuntime = computed(() => Boolean(props.plugin.isMissingRuntime || props.plugin.state === 7));
    const isMissingDependencies = computed(() => Boolean(props.plugin.isMissingDependencies || props.plugin.state === 8));
    const hasMissingPrerequisites = computed(() => isMissingRuntime.value || isMissingDependencies.value);
    const missingPrerequisitesMessage = computed(() => {
      if (props.plugin.missingDependenciesMessage) return props.plugin.missingDependenciesMessage;
      if (props.plugin.missingRuntimeMessage) return props.plugin.missingRuntimeMessage;
      return '缺少运行环境或必要依赖';
    });
    const isAutoStart = computed(() => props.plugin.autoStart !== false);

    const runtimeMeta = computed(() => PluginStateMachine.getRuntimeMeta(props.plugin.runtime));
    const stateMeta = computed(() => PluginStateMachine.getStateMeta(props.plugin));
    const canStart = computed(() => PluginStateMachine.canStart(props.plugin));
    const canStop = computed(() => PluginStateMachine.canStop(props.plugin));
    const canRestart = computed(() => PluginStateMachine.canRestart(props.plugin));

    const hasWebview = computed(() => PluginUrlResolver.hasWebview(props.plugin));
    const hasHttpService = computed(() => PluginUrlResolver.hasIndependentHttpGateway(props.plugin));
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
    function handleExecute() {
      if (customActions.value && customActions.value.length > 0) {
        emit('action', { plugin: props.plugin, action: customActions.value[0] });
      } else {
        emit('action', {
          plugin: props.plugin,
          action: { action: 'default', label: props.plugin.name || '默认执行', icon: '⚡' }
        });
      }
    }
    function handleInstallGuide() { emit('install-guide', props.plugin); }
    function handleSwitchPort() { emit('switch-port', props.plugin); }

    function openGateway() {
      PluginUrlResolver.openPluginGateway(props.plugin, auth.token, toast);
    }

    return {
      isResident,
      isRunning,
      isStarting,
      isDegradedOrError,
      isMissingRuntime,
      isMissingDependencies,
      hasMissingPrerequisites,
      missingPrerequisitesMessage,
      isAutoStart,
      runtimeMeta,
      stateMeta,
      canStart,
      canStop,
      canRestart,
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
      handleExecute,
      handleInstallGuide,
      handleSwitchPort,
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
            <div class="flex items-center gap-1">
              <span v-if="isResident" class="text-[9px] px-1.5 py-0.5 rounded border"
                :class="isAutoStart ? 'bg-emerald-500/10 border-emerald-500/30 text-emerald-300' : 'bg-slate-800 border-slate-700 text-slate-400'"
                :title="isAutoStart ? '开机自启动已启用' : '已关闭开机自启动'">
                {{ isAutoStart ? '⚡ 自启' : '⏸ 手动' }}
              </span>
              <span class="text-[10px] px-1.5 py-0.5 rounded border" :class="runtimeMeta.color">
                {{ runtimeMeta.icon }} {{ runtimeMeta.label }}
              </span>
            </div>
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
          <div v-if="isResident" class="text-slate-400 truncate flex items-center gap-1">
            <span>端口:</span>
            <span v-if="plugin.allocatedPort"
              class="font-mono font-medium inline-flex items-center gap-1 cursor-pointer hover:underline"
              :class="plugin.isPortAvoided ? 'text-amber-400' : 'text-cyan-300'"
              :title="plugin.isPortAvoided ? (plugin.portConflictReason || '声明端口冲突，已自动避让') : '点击快速切换端口'"
              @click="handleSwitchPort()">
              :{{ plugin.allocatedPort }}
              <span v-if="plugin.isPortAvoided" class="text-[9px] px-1 py-0.2 rounded bg-amber-500/20 border border-amber-500/40 text-amber-300">避让</span>
              <span class="text-[10px] opacity-60 hover:opacity-100">🔀</span>
            </span>
            <span v-else-if="plugin.manifest?.network?.port"
              class="font-mono text-slate-400 inline-flex items-center gap-1 cursor-pointer hover:underline"
              title="预设端口（未启动），点击快速切换"
              @click="handleSwitchPort()">
              :{{ plugin.manifest.network.port }}
              <span class="text-[10px] opacity-60 hover:opacity-100">⚙️</span>
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

        <!-- 降级/崩溃告警横幅与原因透出 -->
        <div v-if="isDegradedOrError && (plugin.statusMessage || plugin.lastError)"
          class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 text-[11px] text-amber-300">
          <div class="font-semibold flex items-center gap-1 text-amber-200 mb-0.5">
            <span>⚠️</span>
            <span>{{ plugin.state === 5 ? '服务已降级（熔断保护）' : (plugin.state === 3 ? '服务异常崩溃' : '重试等待中') }}</span>
          </div>
          <div class="font-mono break-all opacity-90" :title="plugin.statusMessage || plugin.lastError">
            {{ plugin.statusMessage || plugin.lastError }}
          </div>
        </div>

        <!-- 一般异常提示（如启动失败、停止失败） -->
        <div v-else-if="(plugin.statusMessage || plugin.lastError) && !isRunning && !hasMissingPrerequisites"
          class="mt-2 text-[11px] text-red-400 bg-red-950/30 border border-red-800/40 rounded px-2 py-1 line-clamp-2"
          :title="plugin.statusMessage || plugin.lastError">
          ⚠️ {{ plugin.statusMessage || plugin.lastError }}
        </div>

        <!-- 缺失运行环境或二进制依赖引导横幅 -->
        <div v-if="hasMissingPrerequisites" class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 flex items-center justify-between gap-2">
          <div class="text-[11px] text-amber-300 font-mono truncate" :title="missingPrerequisitesMessage">
            ⚠️ {{ missingPrerequisitesMessage }}
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
            class="btn-ghost px-2 py-1 text-xs text-emerald-400 hover:bg-emerald-500/10 rounded flex items-center gap-1 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="!canStart"
            @click="handleStart()"
          >
            <span>{{ isDegradedOrError ? '🔄' : '▶' }}</span>
            <span>{{ isDegradedOrError ? '重新拉起' : '启动' }}</span>
          </button>
          <button
            v-if="canStop && (isRunning || isDegradedOrError)"
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-orange-400 hover:bg-orange-500/10 rounded flex items-center gap-1 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="!canStop"
            @click="handleStop()"
          >
            <span>⏹</span>
            <span>停止</span>
          </button>
          <button
            v-if="isRunning"
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-cyan-400 hover:bg-cyan-500/10 rounded flex items-center gap-1 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="!canRestart"
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
            class="btn-ghost px-2 py-1 text-xs text-emerald-400 hover:bg-emerald-500/10 rounded flex items-center gap-1 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="plugin.manifest?.enabled === false"
            title="运行/触发一次性脚本任务"
            @click="handleExecute()"
          >
            <span>▶</span>
            <span>运行</span>
          </button>
          <button
            type="button"
            class="btn-ghost px-2 py-1 text-xs text-slate-300 hover:bg-slate-800 rounded flex items-center gap-1"
            title="切换脚本扩展启用状态"
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
            type="button"
            class="btn-ghost p-1 text-slate-400 hover:text-blue-300 rounded"
            title="在线文件管理与代码编辑"
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
