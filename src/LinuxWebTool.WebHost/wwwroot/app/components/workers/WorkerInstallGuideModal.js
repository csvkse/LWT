import { defineComponent, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'WorkerInstallGuideModal',
  props: {
    show: { type: Boolean, default: false },
    worker: { type: Object, default: () => ({}) },
    env: { type: Object, default: () => ({}) },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const router = useRouter();
    const editedCommand = ref('');
    const selectedTarget = ref('container');

    watch(() => props.worker, (val) => {
      if (val) {
        editedCommand.value = val.installGuideText || '';
        selectedTarget.value = 'container';
      }
    }, { immediate: true });

    function closeModal() {
      emit('close');
    }

    function selectDistroCmd(cmd) {
      if (cmd) editedCommand.value = cmd;
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
      router,
      editedCommand,
      selectedTarget,
      closeModal,
      selectDistroCmd,
      runInTerminal,
      copyGuide,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-xl w-full p-5 flex flex-col gap-4 border-amber-500/40 animate-fade-in max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">🛠️</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">函数微服务依赖安装引导</div>
              <div class="text-[11px] text-amber-400 font-mono mt-0.5">
                {{ worker?.name }} ({{ worker?.id }})
              </div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <div class="text-xs text-slate-300 space-y-2">
          <div v-if="worker?.missingDependencies && worker.missingDependencies.length > 0" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300">
            ⚠️ 缺少解释器运行时: <span class="font-mono font-bold">{{ worker.missingDependencies.join(', ') }}</span>
          </div>

          <p class="text-slate-400 text-[11px] pt-1">
            当前函数微服务声明了必需的语言运行时。您可以在下方编辑安装指令并直接在 Web 终端中执行：
          </p>
        </div>

        <!-- 执行目标切换 (特权容器下可见) -->
        <div v-if="env?.isPrivilegedContainer" class="p-2.5 rounded bg-slate-900/60 border border-cyber-line/50 text-xs flex flex-col gap-1.5">
          <div class="flex items-center justify-between">
            <span class="text-slate-400 font-medium">安装目标对象 (Target):</span>
            <span class="text-[10px] text-cyan-400 font-mono">特权容器双向支持</span>
          </div>
          <div class="flex items-center gap-4">
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
              <input type="radio" v-model="selectedTarget" value="container" class="accent-cyan-400" />
              <span>📦 当前容器 (推荐)</span>
            </label>
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
              <input type="radio" v-model="selectedTarget" value="host" class="accent-cyan-400" />
              <span>🖥️ 宿主主机</span>
            </label>
          </div>
        </div>

        <!-- 跨宿主安全风险告警守卫 -->
        <div v-if="env?.isPrivilegedContainer && selectedTarget === 'host'" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-xs space-y-1">
          <div class="font-semibold flex items-center gap-1">
            <span>⚠️</span>
            <span>运行环境隔离提示</span>
          </div>
          <div class="text-[11px] text-amber-200/90 leading-relaxed">
            当前微服务由容器内部进程管理器调度，若仅在宿主系统上安装依赖，容器内的微服务仍将因命名空间隔离而无法识别该解释器。建议安装至「当前容器」；若确需宿主环境，请确认对应配置。
          </div>
        </div>

        <!-- 安装命令编辑区 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span>推荐安装命令 (支持在线编辑):</span>
            <button
              v-if="editedCommand"
              type="button"
              class="text-xs text-cyan-400 hover:text-cyan-300 underline"
              @click="copyGuide(editedCommand)"
            >
              📋 复制命令
            </button>
          </div>
          <textarea
            v-model="editedCommand"
            rows="2"
            class="input font-mono text-xs w-full text-amber-300 bg-cyber-bg/90 border-cyber-line/60 select-all"
            placeholder="请输入或调整安装命令..."
          ></textarea>
        </div>

        <!-- 常用发行版快速参考 -->
        <div class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="text-xs font-semibold text-slate-300">各主流发行版快速参考 (点击填入):</div>
          <div class="space-y-1.5 max-h-44 overflow-y-auto text-[11px]">
            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 cursor-pointer hover:border-cyan-500/40"
                 @click="selectDistroCmd('apk add --no-cache ' + (worker?.missingDependencies?.[0] || ''))">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Alpine Linux (Docker 推荐):</span>
                <span class="text-[10px] text-cyan-400">点击填入 ↵</span>
              </div>
              <div class="font-mono text-slate-300">apk add --no-cache {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 cursor-pointer hover:border-cyan-500/40"
                 @click="selectDistroCmd('apt update && apt install -y ' + (worker?.missingDependencies?.[0] || ''))">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Debian / Ubuntu:</span>
                <span class="text-[10px] text-cyan-400">点击填入 ↵</span>
              </div>
              <div class="font-mono text-slate-300">apt update && apt install -y {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 cursor-pointer hover:border-cyan-500/40"
                 @click="selectDistroCmd('dnf install -y ' + (worker?.missingDependencies?.[0] || ''))">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">RHEL / CentOS / Rocky:</span>
                <span class="text-[10px] text-cyan-400">点击填入 ↵</span>
              </div>
              <div class="font-mono text-slate-300">dnf install -y {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 cursor-pointer hover:border-cyan-500/40"
                 @click="selectDistroCmd('winget install ' + (worker?.missingDependencies?.[0] || ''))">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Windows:</span>
                <span class="text-[10px] text-cyan-400">点击填入 ↵</span>
              </div>
              <div class="font-mono text-slate-300">winget install {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>
          </div>
        </div>

        <div class="flex items-center justify-between pt-2 border-t border-cyber-line/50">
          <button type="button" class="btn btn-sm" @click="closeModal()">关闭</button>
          <div class="flex items-center gap-2">
            <button type="button" class="btn btn-sm hover:border-cyan-500/40" @click="copyGuide(editedCommand)">
              📋 复制
            </button>
            <button
              type="button"
              class="btn btn-sm btn-primary flex items-center gap-1.5"
              @click="runInTerminal()"
              title="跳转至 Web 终端自动建立会话并执行"
            >
              <span>💻 在终端中执行</span>
              <span v-if="env?.isPrivilegedContainer" class="text-[10px] px-1 rounded bg-black/30 font-mono">
                {{ selectedTarget === 'host' ? '宿主' : '容器' }}
              </span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
