import { computed, defineComponent } from 'vue';
import { PluginStateMachine } from '../../utils/pluginStateMachine.js';
import { PluginUrlResolver } from '../../utils/pluginUrlResolver.js';
import { auth } from '../../store/auth.js';
import { toast } from '../../store/toast.js';
import PluginCardMetaBadges from './PluginCardMetaBadges.js';

export default defineComponent({
  name: 'PluginCard',
  components: { PluginCardMetaBadges },
  props: { plugin: { type: Object, required: true } },
  emits: ['start', 'stop', 'restart', 'action', 'code', 'logs', 'kv', 'webview', 'delete', 'toggle-enabled', 'install-guide', 'switch-port', 'invoke'],
  setup(props, { emit }) {
    const isResident = computed(() => props.plugin.profile === 0);
    const isServiceLike = computed(() => props.plugin.profile === 0 || props.plugin.profile === 2);
    const profileLabel = computed(() => ['常驻微服务', '一次性脚本', '按需 FaaS', 'Wasm 微函数'][props.plugin.profile] || '一次性脚本');
    const isRunning = computed(() => props.plugin.state === 2);
    const isProbing = computed(() => props.plugin.state === 9);
    const isDegradedOrError = computed(() => [3, 4, 5].includes(props.plugin.state));
    const hasMissingPrerequisites = computed(() => Boolean(props.plugin.isMissingRuntime || props.plugin.isMissingDependencies || props.plugin.state === 7 || props.plugin.state === 8));
    const missingMessage = computed(() => props.plugin.missingDependenciesMessage || props.plugin.missingRuntimeMessage || '缺少运行环境或必要依赖');
    const isAutoStart = computed(() => props.plugin.autoStart !== false);

    const runtimeMeta = computed(() => PluginStateMachine.getRuntimeMeta(props.plugin.runtime));
    const stateMeta = computed(() => PluginStateMachine.getStateMeta(props.plugin));
    const canStart = computed(() => PluginStateMachine.canStart(props.plugin));
    const canStop = computed(() => PluginStateMachine.canStop(props.plugin));
    const canRestart = computed(() => PluginStateMachine.canRestart(props.plugin));

    const hasWebview = computed(() => PluginUrlResolver.hasWebview(props.plugin));
    const hasHttpService = computed(() => PluginUrlResolver.hasIndependentHttpGateway(props.plugin));
    const customActions = computed(() => props.plugin.manifest?.customActions || []);
    const canInvoke = computed(() => Boolean(props.plugin.manifest?.interface?.services?.length > 0 || [0, 2, 3].includes(props.plugin.profile)));

    function handleAction(act) { emit('action', { plugin: props.plugin, action: act }); }
    function handleSwitchPort() { emit('switch-port', props.plugin); }
    function handleExecute() {
      const act = customActions.value[0] || { action: 'default', label: props.plugin.name || '默认执行', icon: '⚡' };
      emit('action', { plugin: props.plugin, action: act });
    }
    function openGateway() { PluginUrlResolver.openPluginGateway(props.plugin, auth.token, toast); }

    return {
      isResident, isServiceLike, profileLabel, isRunning, isProbing, isDegradedOrError, hasMissingPrerequisites, missingMessage,
      isAutoStart, runtimeMeta, stateMeta, canStart, canStop, canRestart, hasWebview, hasHttpService, customActions, canInvoke,
      emit, handleAction, handleSwitchPort, handleExecute, openGateway,
    };
  },
  template: `
    <div class="panel p-4 flex flex-col justify-between h-full border border-cyber-line/50 hover:border-cyan-500/40 transition-colors">
      <div>
        <div class="flex items-start justify-between gap-3">
          <div class="flex items-center gap-2.5 min-w-0">
            <div class="w-10 h-10 rounded-lg flex items-center justify-center text-xl shrink-0 border"
              :class="isResident ? 'bg-amber-500/10 border-amber-500/30 text-amber-300' : 'bg-cyan-500/10 border-cyan-500/30 text-cyan-300'">
              {{ isResident ? '⚡' : '🔌' }}
            </div>
            <div class="min-w-0">
              <div class="flex items-center gap-1.5 flex-wrap">
                <span class="font-display font-semibold text-slate-100 text-sm truncate" :title="plugin.name">{{ plugin.name }}</span>
                <span class="text-[10px] text-slate-400 font-mono">v{{ plugin.manifest?.version || '1.0.0' }}</span>
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate" :title="plugin.id">{{ plugin.id }}</div>
            </div>
          </div>
          <div class="flex flex-col items-end gap-1 shrink-0">
            <span class="text-[10px] px-2 py-0.5 rounded-full font-medium" :class="stateMeta.color">{{ stateMeta.label }}</span>
            <div class="flex items-center gap-1">
              <span v-if="isServiceLike" class="text-[9px] px-1.5 py-0.5 rounded border"
                :class="isAutoStart ? 'bg-emerald-500/10 border-emerald-500/30 text-emerald-300' : 'bg-slate-800 border-slate-700 text-slate-400'">
                {{ isAutoStart ? '⚡ 自启' : '⏸ 手动' }}
              </span>
              <span class="text-[10px] px-1.5 py-0.5 rounded border" :class="runtimeMeta.color">{{ runtimeMeta.icon }} {{ runtimeMeta.label }}</span>
            </div>
          </div>
        </div>

        <p class="text-xs text-slate-300 mt-2.5 line-clamp-2 min-h-[2rem]" :title="plugin.manifest?.description">{{ plugin.manifest?.description || '暂无描述' }}</p>
        <div class="mt-2.5"><PluginCardMetaBadges :plugin="plugin" /></div>

        <div class="mt-3 pt-2 border-t border-cyber-line/40 grid grid-cols-2 gap-2 text-[11px]">
          <div class="text-slate-400 truncate">类型: <span class="text-slate-200 font-medium">{{ profileLabel }}</span></div>
          <div class="text-slate-400 truncate">分类: <span class="text-slate-200">{{ plugin.manifest?.category || '默认' }}</span></div>
          <div v-if="isServiceLike" class="text-slate-400 truncate flex items-center gap-1">
            <span>端口:</span>
            <span v-if="plugin.allocatedPort" class="font-mono font-medium inline-flex items-center gap-1 cursor-pointer hover:underline"
              :class="plugin.isPortAvoided ? 'text-amber-400' : 'text-cyan-300'" @click="handleSwitchPort()">
              :{{ plugin.allocatedPort }}
              <span v-if="plugin.isPortAvoided" class="text-[9px] px-1 py-0.2 rounded bg-amber-500/20 text-amber-300">避让</span>
              <span class="text-[10px] opacity-60">🔀</span>
            </span>
            <span v-else-if="plugin.manifest?.network?.port" class="font-mono text-slate-400 inline-flex items-center gap-1 cursor-pointer hover:underline" @click="handleSwitchPort()">
              :{{ plugin.manifest.network.port }}<span class="text-[10px] opacity-60">⚙️</span>
            </span>
            <span v-else class="text-slate-500">未分配</span>
          </div>
          <div v-if="isResident" class="text-slate-400 truncate">PID: <span class="text-slate-200 font-mono">{{ plugin.processId || '-' }}</span></div>
        </div>

        <div v-if="isDegradedOrError && (plugin.statusMessage || plugin.lastError)" class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 text-[11px] text-amber-300">
          <div class="font-semibold text-amber-200 mb-0.5">⚠️ {{ plugin.state === 5 ? '服务已降级（熔断保护）' : (plugin.state === 3 ? '服务异常崩溃' : '重试等待中') }}</div>
          <div class="font-mono break-all opacity-90">{{ plugin.statusMessage || plugin.lastError }}</div>
        </div>
        <div v-if="hasMissingPrerequisites" class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 flex items-center justify-between gap-2">
          <div class="text-[11px] text-amber-300 font-mono truncate">⚠️ {{ missingMessage }}</div>
          <button type="button" class="text-[11px] text-cyan-400 hover:text-cyan-300 font-medium shrink-0 underline" @click="emit('install-guide', plugin)">🛠️ 安装引导</button>
        </div>

        <div class="mt-3 space-y-2">
          <button v-if="hasWebview" type="button" class="btn-primary w-full py-1.5 text-xs flex items-center justify-center gap-1.5 bg-gradient-to-r from-cyan-600 to-blue-600 text-white font-medium" @click="emit('webview', plugin)">
            <span>🖥️ 打开 Webview 扩展视图</span>
          </button>
          <button v-if="hasHttpService && isRunning" type="button" class="btn-secondary w-full py-1.5 text-xs flex items-center justify-center gap-1.5 text-cyan-300 hover:bg-cyan-500/10 border-cyan-500/30" @click="openGateway()">
            <span>🌐 访问微服务网页 / 接口 ↗</span>
          </button>
          <div v-if="customActions.length > 0" class="flex flex-wrap gap-1.5">
            <button v-for="act in customActions" :key="act.action || act.name" type="button" class="btn-ghost py-1 px-2 text-[11px] rounded border border-cyber-line hover:border-cyan-500/40 text-slate-200" @click="handleAction(act)">
              <span>{{ act.icon || '⚡' }} {{ act.label || act.name || act.action }}</span>
            </button>
          </div>
        </div>
      </div>

      <div class="mt-4 pt-3 border-t border-cyber-line/50 flex items-center justify-between gap-1 text-xs">
        <div v-if="isServiceLike" class="flex items-center gap-1">
          <button v-if="isProbing" type="button" class="btn-ghost px-2 py-1 text-xs text-indigo-300 bg-indigo-500/10 rounded cursor-wait" disabled>
            <span>⏳ 探测中…</span>
          </button>
          <button v-else-if="!isRunning" type="button" class="btn-ghost px-2 py-1 text-xs text-emerald-400 hover:bg-emerald-500/10 rounded disabled:opacity-40" :disabled="!canStart" @click="emit('start', plugin)">
            <span>{{ isDegradedOrError ? '🔄' : '▶' }}</span>
            <span>{{ isDegradedOrError ? '重新拉起' : '启动' }}</span>
          </button>
          <button v-if="canStop && (isRunning || isDegradedOrError || isProbing)" type="button" class="btn-ghost px-2 py-1 text-xs text-orange-400 hover:bg-orange-500/10 rounded disabled:opacity-40" :disabled="!canStop" @click="emit('stop', plugin)">
            <span>⏹ 停止</span>
          </button>
          <button v-if="isRunning" type="button" class="btn-ghost px-2 py-1 text-xs text-cyan-400 hover:bg-cyan-500/10 rounded disabled:opacity-40" :disabled="!canRestart" @click="emit('restart', plugin)">
            <span>🔄 重启</span>
          </button>
        </div>
        <div v-else class="flex items-center gap-1">
          <button type="button" class="btn-ghost px-2 py-1 text-xs text-emerald-400 hover:bg-emerald-500/10 rounded disabled:opacity-40" :disabled="plugin.manifest?.enabled === false" @click="handleExecute()">
            <span>▶</span>
            <span>运行</span>
          </button>
          <button type="button" class="btn-ghost px-2 py-1 text-xs text-slate-300 hover:bg-slate-800 rounded" @click="emit('toggle-enabled', plugin)">
            <span>{{ plugin.manifest?.enabled !== false ? '🟢 已启用' : '⚪ 已禁用' }}</span>
          </button>
        </div>
        <div class="flex items-center gap-1 ml-auto">
          <button v-if="canInvoke" type="button" class="btn-ghost px-1.5 py-1 text-cyan-300 hover:bg-cyan-500/10 rounded text-[11px]" title="透明反射调用该插件服务" @click="emit('invoke', plugin)">
            <span>📡 调用</span>
          </button>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-amber-300 rounded" title="环境依赖与安装指引" @click="emit('install-guide', plugin)"><span>🛠️</span></button>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-cyan-300 rounded" title="查看实时运行日志" @click="emit('logs', plugin)"><span>📜</span></button>
          <button v-if="plugin.manifest?.storage?.enableKv" type="button" class="btn-ghost p-1 text-slate-400 hover:text-amber-300 rounded" title="KV 存储空间" @click="emit('kv', plugin)"><span>🗄️</span></button>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-blue-300 rounded" title="在线文件管理与代码编辑" @click="emit('code', plugin)"><span>✏️</span></button>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-red-400 rounded" title="卸载插件" @click="emit('delete', plugin)"><span>🗑️</span></button>
        </div>
      </div>
    </div>
  `,
});
