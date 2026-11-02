import { computed, defineComponent, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from '../../store/toast.js';
import {
  DISTRO_TABS,
  RUNTIME_NAMES,
  buildAggregatedCommand,
} from './pluginCommandBuilder.js';

export default defineComponent({
  name: 'PluginInstallGuideModal',
  props: {
    show: { type: Boolean, default: false },
    plugin: { type: Object, default: () => ({}) },
    env: { type: Object, default: () => ({}) },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const router = useRouter();
    const selectedDistro = ref('alpine');
    const selectedDeps = ref(new Set());
    const includeCoreRuntime = ref(false);
    const editedCommand = ref('');
    const selectedTarget = ref('container');

    const runtimeName = computed(() => {
      const rt = props.plugin?.runtime;
      return RUNTIME_NAMES[rt] || '未知运行时';
    });

    const isPrivilegedContainer = computed(() => {
      return props.env?.isPrivilegedContainer === true;
    });

    const dependencyList = computed(() => {
      const deps = props.plugin?.dependencies || props.plugin?.manifest?.dependencies;
      if (Array.isArray(deps)) return deps;
      if (deps && typeof deps === 'object' && Array.isArray(deps.items)) return deps.items;
      return [];
    });

    function updateAggregatedCommand() {
      editedCommand.value = buildAggregatedCommand(
        props.plugin,
        dependencyList.value,
        selectedDistro.value,
        selectedDeps.value,
        includeCoreRuntime.value
      );
    }

    function isDepSelected(id) {
      return selectedDeps.value.has(id);
    }

    function toggleDep(dep) {
      if (dep.required) return;
      const set = new Set(selectedDeps.value);
      if (set.has(dep.id)) {
        set.delete(dep.id);
      } else {
        set.add(dep.id);
      }
      selectedDeps.value = set;
      updateAggregatedCommand();
    }

    function selectDistro(distroKey) {
      selectedDistro.value = distroKey;
      updateAggregatedCommand();
    }

    function toggleCoreRuntime() {
      includeCoreRuntime.value = !includeCoreRuntime.value;
      updateAggregatedCommand();
    }

    function resetCommand() {
      const set = new Set();
      for (const dep of dependencyList.value) {
        if (dep.required || (dep.recommended && !dep.isInstalled)) {
          set.add(dep.id);
        }
      }
      selectedDeps.value = set;
      includeCoreRuntime.value = props.plugin?.isMissingRuntime === true;
      updateAggregatedCommand();
    }

    watch(
      () => [props.show, props.plugin],
      ([show, p]) => {
        if (show && p) {
          selectedDistro.value = 'alpine';
          selectedTarget.value = 'container';
          resetCommand();
        }
      },
      { immediate: true }
    );

    function closeModal() {
      emit('close');
    }

    function runInTerminal() {
      if (!editedCommand.value || !editedCommand.value.trim()) {
        toast.info('安装命令为空');
        return;
      }
      router.push({
        path: '/terminal',
        query: {
          exec: editedCommand.value.trim(),
          target: selectedTarget.value,
        },
      });
      closeModal();
    }

    async function copyGuide(text) {
      const content = text || editedCommand.value;
      if (!content) return;
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          await navigator.clipboard.writeText(content);
        } else {
          const ta = document.createElement('textarea');
          ta.value = content;
          document.body.appendChild(ta);
          ta.select();
          document.execCommand('copy');
          document.body.removeChild(ta);
        }
        toast.success('安装命令已成功复制到剪贴板');
      } catch (err) {
        toast.error('复制失败: ' + err.message);
      }
    }

    return {
      DISTRO_TABS,
      runtimeName,
      isPrivilegedContainer,
      selectedDistro,
      selectedDeps,
      includeCoreRuntime,
      editedCommand,
      selectedTarget,
      dependencyList,
      isDepSelected,
      toggleDep,
      selectDistro,
      toggleCoreRuntime,
      resetCommand,
      closeModal,
      runInTerminal,
      copyGuide,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm animate-fade-in">
      <div class="panel max-w-2xl w-full p-5 flex flex-col gap-4 border-amber-500/40 animate-scale-up max-h-[92vh] overflow-y-auto">
        <!-- 弹窗标题栏 -->
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2.5">
            <div class="w-8 h-8 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 flex items-center justify-center text-lg">
              🛠️
            </div>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">插件运行环境与依赖安装引导</div>
              <div class="text-[11px] text-amber-400 font-mono mt-0.5">
                {{ plugin?.name }} (v{{ plugin?.manifest?.version || '1.0.0' }}) · 目标运行时: {{ runtimeName }}
              </div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <!-- 发行版选择 Tab 栏 -->
        <div class="flex items-center gap-1.5 p-1 bg-slate-900/80 rounded border border-cyber-line/50 text-xs overflow-x-auto">
          <button
            v-for="tab in DISTRO_TABS"
            :key="tab.key"
            type="button"
            class="px-2.5 py-1.5 rounded transition-all whitespace-nowrap text-xs flex items-center gap-1"
            :class="selectedDistro === tab.key ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40 font-medium shadow-sm' : 'text-slate-400 hover:text-slate-200'"
            @click="selectDistro(tab.key)"
          >
            <span>{{ tab.label }}</span>
          </button>
        </div>

        <!-- 区域 1：核心运行时检测与勾选 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs font-semibold text-slate-300">
            <span>🚀 1. 核心语言运行时 (Core Runtime)</span>
            <span
              v-if="plugin?.isMissingRuntime"
              class="px-1.5 py-0.5 rounded text-[10px] bg-amber-500/20 text-amber-300 border border-amber-500/30 font-mono"
            >
              ⚠️ 缺失待安装
            </span>
            <span
              v-else
              class="px-1.5 py-0.5 rounded text-[10px] bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-mono"
            >
              ✅ 已就绪
            </span>
          </div>

          <div
            class="p-2.5 rounded border transition-colors cursor-pointer flex items-start gap-2.5"
            :class="includeCoreRuntime ? 'bg-cyan-950/30 border-cyan-500/40' : 'bg-slate-900/60 border-slate-700/50'"
            @click="toggleCoreRuntime()"
          >
            <input
              type="checkbox"
              :checked="includeCoreRuntime"
              class="mt-0.5 accent-cyan-400 cursor-pointer pointer-events-none"
            />
            <div class="space-y-0.5 flex-1">
              <div class="flex items-center justify-between text-xs">
                <span class="font-medium text-slate-200">{{ runtimeName }} 解释器与标准库</span>
                <span class="text-[10px] text-slate-400 font-mono">
                  {{ plugin?.isMissingRuntime ? '推荐勾选安装' : '环境已存在 (如需重装可勾选)' }}
                </span>
              </div>
              <div class="text-[11px] text-slate-400 leading-relaxed font-mono">
                {{ plugin?.missingRuntimeMessage || '提供该插件主进程启动运行的基础环境。' }}
              </div>
            </div>
          </div>
        </div>

        <!-- 区域 2：扩展工具链与可选依赖 (Ecosystem Dependencies) -->
        <div v-if="dependencyList.length > 0" class="space-y-2">
          <div class="flex items-center justify-between text-xs font-semibold text-slate-300">
            <span>📦 2. 可选生态工具链与下游依赖 (Optional Dependencies)</span>
            <span class="text-[10px] text-cyan-400 font-mono">勾选后自动聚合为原子安装指令</span>
          </div>

          <div class="space-y-1.5 max-h-48 overflow-y-auto pr-1">
            <div
              v-for="dep in dependencyList"
              :key="dep.id"
              class="p-2.5 rounded border transition-colors cursor-pointer flex items-start gap-2.5"
              :class="isDepSelected(dep.id) ? 'bg-cyan-950/30 border-cyan-500/40' : 'bg-slate-900/60 border-slate-700/50'"
              @click="toggleDep(dep)"
            >
              <input
                type="checkbox"
                :checked="isDepSelected(dep.id)"
                :disabled="dep.required"
                class="mt-0.5 accent-cyan-400 cursor-pointer pointer-events-none"
              />
              <div class="space-y-1 flex-1">
                <div class="flex items-center justify-between text-xs">
                  <div class="flex items-center gap-1.5">
                    <span class="font-medium text-slate-100">{{ dep.name }}</span>
                    <span
                      v-if="dep.required"
                      class="px-1 py-0.2 rounded text-[9px] bg-rose-500/20 text-rose-300 border border-rose-500/30"
                    >必需</span>
                    <span
                      v-else-if="dep.recommended"
                      class="px-1 py-0.2 rounded text-[9px] bg-cyan-500/20 text-cyan-300 border border-cyan-500/30"
                    >推荐</span>
                  </div>
                  <span
                    v-if="dep.isInstalled"
                    class="px-1.5 py-0.2 rounded text-[10px] bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-mono"
                  >已就绪</span>
                  <span
                    v-else
                    class="px-1.5 py-0.2 rounded text-[10px] bg-amber-500/20 text-amber-300 border border-amber-500/30 font-mono"
                  >待安装</span>
                </div>
                <div class="text-[11px] text-slate-400 leading-relaxed font-mono">
                  {{ dep.description }}
                </div>
                <div
                  v-if="dep.installGuide && dep.installGuide[selectedDistro]"
                  class="text-[10px] text-cyan-400 font-mono bg-black/40 px-2 py-0.5 rounded truncate"
                  :title="dep.installGuide[selectedDistro]"
                >
                  $ {{ dep.installGuide[selectedDistro] }}
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 区域 3：执行目标切换器（特权容器双向可见） -->
        <div v-if="isPrivilegedContainer" class="p-2.5 rounded bg-slate-900/60 border border-cyber-line/50 text-xs flex flex-col gap-1.5">
          <div class="flex items-center justify-between">
            <span class="text-slate-300 font-medium">🎯 3. 执行目标环境 (Target Scope):</span>
            <span class="text-[10px] text-cyan-400 font-mono">特权容器双向穿透支持</span>
          </div>
          <div class="flex items-center gap-4 mt-0.5">
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300 text-xs">
              <input type="radio" v-model="selectedTarget" value="container" class="accent-cyan-400" />
              <span>📦 当前容器 (推荐)</span>
            </label>
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300 text-xs">
              <input type="radio" v-model="selectedTarget" value="host" class="accent-cyan-400" />
              <span>🖥️ 宿主主机</span>
            </label>
          </div>
        </div>

        <!-- 跨宿主安全风险告警守卫 -->
        <div v-if="isPrivilegedContainer && selectedTarget === 'host'" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-xs space-y-1">
          <div class="font-semibold flex items-center gap-1">
            <span>⚠️</span>
            <span>运行环境隔离提示</span>
          </div>
          <div class="text-[11px] text-amber-200/90 leading-relaxed">
            当前插件由 LinuxWebTool 容器内部进程调度。若仅在宿主系统上安装依赖，容器内部仍将因文件系统与命名空间隔离而无法识别该解释器。建议安装至「当前容器」；若确需宿主环境，请确保容器挂载了对应二进制目录。
          </div>
        </div>

        <!-- 区域 4：聚合安装命令编辑区 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span class="font-semibold text-slate-300">
              ⚡️ 4. 聚合安装执行命令 (已自动去重合并包管理器锁，支持在线编辑):
            </span>
            <button
              type="button"
              class="text-xs text-cyan-400 hover:text-cyan-200"
              @click="resetCommand()"
              title="重置为默认推荐命令"
            >
              ↺ 重置
            </button>
          </div>
          <textarea
            v-model="editedCommand"
            rows="3"
            placeholder="输入或调整要在终端中执行的安装命令..."
            class="w-full p-2.5 bg-cyber-bg/90 rounded border border-cyber-line/60 font-mono text-xs text-amber-300 focus:outline-none focus:border-cyan-500 leading-relaxed select-all"
          ></textarea>
        </div>

        <!-- 底部操作按钮 -->
        <div class="flex items-center justify-between pt-2 border-t border-cyber-line/40">
          <button type="button" class="btn btn-sm hover:border-slate-500" @click="closeModal()">
            关闭
          </button>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn btn-sm hover:border-cyan-500"
              @click="copyGuide(editedCommand)"
            >
              📋 复制命令
            </button>
            <button
              type="button"
              class="btn btn-sm btn-primary flex items-center gap-1.5"
              @click="runInTerminal()"
              title="跳转至 Web 终端自动建立会话并执行"
            >
              <span>💻 在终端中执行</span>
              <span v-if="isPrivilegedContainer" class="text-[10px] opacity-80 font-mono">
                ({{ selectedTarget === 'host' ? '宿主' : '容器' }})
              </span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
