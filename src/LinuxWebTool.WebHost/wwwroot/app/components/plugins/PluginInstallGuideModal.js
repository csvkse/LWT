import { computed, defineComponent, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from '../../store/toast.js';

const RUNTIME_NAMES = {
  0: 'Node.js',
  1: 'Python 3',
  2: '.NET SDK',
  3: 'Bash',
  4: 'PowerShell',
  5: 'Native',
};

const RUNTIME_DEFAULT_COMMANDS = {
  0: {
    alpine: 'apk add --no-cache nodejs npm',
    debian: 'apt update && apt install -y nodejs npm',
    rhel: 'dnf install -y nodejs',
    windows: 'winget install OpenJS.NodeJS.LTS',
  },
  1: {
    alpine: 'apk add --no-cache python3 py3-pip',
    debian: 'apt update && apt install -y python3 python3-pip',
    rhel: 'dnf install -y python3 python3-pip',
    windows: 'winget install Python.Python.3.12',
  },
  2: {
    alpine: 'apk add --no-cache dotnet8-sdk',
    debian: 'apt update && apt install -y dotnet-sdk-8.0',
    rhel: 'dnf install -y dotnet-sdk-8.0',
    windows: 'winget install Microsoft.DotNet.SDK.8',
  },
  3: {
    alpine: 'apk add --no-cache bash',
    debian: 'apt update && apt install -y bash',
    rhel: 'dnf install -y bash',
    windows: 'winget install Git.Git',
  },
  4: {
    alpine: 'apk add --no-cache powershell',
    debian: 'apt update && apt install -y powershell',
    rhel: 'dnf install -y powershell',
    windows: 'winget install Microsoft.PowerShell',
  },
  5: {
    alpine: 'apk add --no-cache caddy curl wget',
    debian: 'apt update && apt install -y caddy curl wget',
    rhel: 'dnf install -y caddy curl wget',
    windows: 'winget install CaddyServer.Caddy',
  },
};

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
    const editedCommand = ref('');
    const selectedTarget = ref('container');

    const runtimeName = computed(() => {
      const rt = props.plugin?.runtime;
      return RUNTIME_NAMES[rt] || '未知运行时';
    });

    const isPrivilegedContainer = computed(() => {
      return props.env?.isPrivilegedContainer === true;
    });

    function resolveDefaultCommand(p) {
      if (!p) return '';
      if (p.effectiveInstallGuide) return p.effectiveInstallGuide;
      const guideMap = p.manifest?.installGuide;
      if (guideMap && typeof guideMap === 'object') {
        if (guideMap.alpine) return guideMap.alpine;
        if (guideMap.linux) return guideMap.linux;
        const firstKey = Object.keys(guideMap)[0];
        if (firstKey) return guideMap[firstKey];
      }
      const rt = p.runtime ?? p.manifest?.runtime ?? 0;
      const defaults = RUNTIME_DEFAULT_COMMANDS[rt] || RUNTIME_DEFAULT_COMMANDS[0];
      return defaults.alpine || defaults.debian || '';
    }

    watch(
      () => [props.show, props.plugin],
      ([show, p]) => {
        if (show && p) {
          editedCommand.value = resolveDefaultCommand(p);
          selectedTarget.value = 'container';
        }
      },
      { immediate: true }
    );

    function closeModal() {
      emit('close');
    }

    function selectDistroCmd(cmd) {
      if (cmd) editedCommand.value = cmd;
    }

    function resetCommand() {
      editedCommand.value = resolveDefaultCommand(props.plugin);
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

    const distroOptions = computed(() => {
      const p = props.plugin;
      const list = [];
      const rt = p?.runtime ?? p?.manifest?.runtime ?? 0;
      const defaults = RUNTIME_DEFAULT_COMMANDS[rt] || RUNTIME_DEFAULT_COMMANDS[0];

      list.push({ distro: 'Alpine Linux (Docker 推荐)', cmd: defaults.alpine });
      list.push({ distro: 'Debian / Ubuntu', cmd: defaults.debian });
      list.push({ distro: 'RHEL / CentOS / Rocky', cmd: defaults.rhel });
      list.push({ distro: 'Windows', cmd: defaults.windows });

      if (p?.manifest?.installGuide) {
        for (const [k, v] of Object.entries(p.manifest.installGuide)) {
          if (!list.some((item) => item.distro.toLowerCase().includes(k.toLowerCase()))) {
            list.push({ distro: k, cmd: v });
          }
        }
      }
      return list;
    });

    return {
      runtimeName,
      isPrivilegedContainer,
      editedCommand,
      selectedTarget,
      distroOptions,
      closeModal,
      selectDistroCmd,
      resetCommand,
      runInTerminal,
      copyGuide,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm animate-fade-in">
      <div class="panel max-w-xl w-full p-5 flex flex-col gap-4 border-amber-500/40 animate-scale-up max-h-[92vh] overflow-y-auto">
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

        <!-- 缺失依赖提示区域 -->
        <div class="text-xs text-slate-300 space-y-2">
          <div v-if="plugin?.isMissingRuntime || plugin?.missingRuntimeMessage" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 flex items-start gap-2">
            <span class="text-base shrink-0">⚠️</span>
            <div class="space-y-1">
              <div class="font-semibold text-amber-200">检测到环境依赖缺失</div>
              <div class="text-[11px] text-amber-300/90 leading-relaxed font-mono">
                {{ plugin.missingRuntimeMessage || '系统中未检测到该插件所需的语言运行时或宿主执行程序。' }}
              </div>
            </div>
          </div>

          <div v-else class="p-2 rounded bg-cyan-500/10 border border-cyan-500/20 text-cyan-300 text-[11px] flex items-center gap-1.5">
            <span>ℹ️</span>
            <span>当前插件运行时已就绪或为内置程序。如需重新安装或补充依赖工具包，可在下方选择安装指令。</span>
          </div>

          <p class="text-slate-400 text-[11px] pt-1">
            当前插件声明了必需的环境解释器或命令行工具。您可在线编辑安装指令，选择执行目标并在 Web 终端中一键执行：
          </p>
        </div>

        <!-- 执行目标切换器（特权容器双向可见） -->
        <div v-if="isPrivilegedContainer" class="p-2.5 rounded bg-slate-900/60 border border-cyber-line/50 text-xs flex flex-col gap-1.5">
          <div class="flex items-center justify-between">
            <span class="text-slate-300 font-medium">🎯 执行目标环境 (Target Scope):</span>
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

        <!-- 安装命令编辑区 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span>安装执行命令 (支持在线编辑调整):</span>
            <button
              type="button"
              class="text-xs text-slate-400 hover:text-slate-200"
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

        <!-- 各主流发行版快速参考与点击填入 -->
        <div class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="text-xs font-semibold text-slate-300">主流 Linux 发行版与环境快速参考 (点击自动填入):</div>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-1.5 max-h-40 overflow-y-auto text-[11px]">
            <div
              v-for="item in distroOptions"
              :key="item.distro"
              class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 cursor-pointer hover:border-cyan-500/50 hover:bg-slate-800/80 transition-colors"
              @click="selectDistroCmd(item.cmd)"
            >
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-medium">{{ item.distro }}:</span>
                <span class="text-[10px] text-slate-400">点击填入 ↵</span>
              </div>
              <div class="font-mono text-slate-300 truncate" :title="item.cmd">{{ item.cmd }}</div>
            </div>
          </div>
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
