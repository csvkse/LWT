import { defineComponent, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionInstallGuideModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: () => ({}) },
    env: { type: Object, default: () => ({}) },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const router = useRouter();
    const editedCommand = ref('');
    const selectedTarget = ref('container');

    watch(() => props.ext, (val) => {
      if (val) {
        editedCommand.value = val.installGuideText || '';
        selectedTarget.value = val.target === 'host' || val.manifest?.target === 'host' ? 'host' : 'container';
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
              <div class="font-display font-semibold text-slate-100 text-sm">环境依赖安装引导</div>
              <div class="text-[11px] text-amber-400 font-mono mt-0.5">
                {{ ext?.manifest?.name }} (v{{ ext?.manifest?.version }})
              </div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <div class="text-xs text-slate-300 space-y-2">
          <div v-if="ext?.missingRunners && ext.missingRunners.length > 0" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300">
            ⚠️ 缺少解释器运行时: <span class="font-mono font-bold">{{ ext.missingRunners.join(', ') }}</span>
          </div>
          <div v-if="ext?.missingBinaries && ext.missingBinaries.length > 0" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300">
            ⚠️ 缺少宿主可执行程序: <span class="font-mono font-bold">{{ ext.missingBinaries.join(', ') }}</span>
          </div>

          <p class="text-slate-400 text-[11px] pt-1">
            当前扩展包声明了必需的环境依赖。您可在此编辑安装命令，选择执行对象并在终端中一键执行：
          </p>
        </div>

        <!-- 执行对象选择器（特权容器下支持切换宿主/容器） -->
        <div v-if="env?.isPrivilegedContainer" class="flex items-center justify-between p-2.5 rounded bg-slate-900/60 border border-cyan-500/20 text-xs">
          <div class="flex items-center gap-1.5 text-slate-300">
            <span>🎯 执行目标:</span>
            <span class="text-[11px] text-cyan-400">已检测到特权容器环境</span>
          </div>
          <div class="flex items-center gap-1">
            <button
              type="button"
              class="px-2.5 py-1 rounded text-xs transition-colors"
              :class="selectedTarget === 'host' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/50 font-semibold' : 'text-slate-400 hover:text-slate-200'"
              @click="selectedTarget = 'host'"
            >
              🖥️ 宿主主机
            </button>
            <button
              type="button"
              class="px-2.5 py-1 rounded text-xs transition-colors"
              :class="selectedTarget === 'container' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/50 font-semibold' : 'text-slate-400 hover:text-slate-200'"
              @click="selectedTarget = 'container'"
            >
              📦 当前容器
            </button>
          </div>
        </div>

        <!-- 可编辑安装命令文本框 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span>安装执行命令 (可在线编辑调整):</span>
            <button
              type="button"
              class="text-xs text-slate-400 hover:text-slate-200"
              @click="editedCommand = ext?.installGuideText || ''"
              title="重置为默认推荐命令"
            >
              ↺ 重置
            </button>
          </div>
          <textarea
            v-model="editedCommand"
            rows="3"
            placeholder="输入或调整要在终端中执行的安装命令..."
            class="w-full p-2.5 bg-cyber-bg/90 rounded border border-cyber-line/60 font-mono text-xs text-amber-300 focus:outline-none focus:border-cyan-500 leading-relaxed"
          ></textarea>
        </div>

        <!-- 多发行版快速切换 -->
        <div v-if="ext?.manifest?.installGuide && Object.keys(ext.manifest.installGuide).length > 0" class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="text-xs font-semibold text-slate-300">快速填入其他 Linux 发行版命令:</div>
          <div class="flex flex-wrap gap-1.5 max-h-32 overflow-y-auto">
            <button
              v-for="(cmd, distro) in ext.manifest.installGuide"
              :key="distro"
              type="button"
              class="px-2 py-1 rounded bg-slate-800 hover:bg-slate-700 border border-slate-600/60 text-[11px] text-cyan-300 capitalize"
              @click="selectDistroCmd(cmd)"
            >
              {{ distro }}
            </button>
          </div>
        </div>

        <div class="flex items-center justify-between pt-2 border-t border-cyber-line/30">
          <button type="button" class="btn btn-sm hover:border-slate-500" @click="closeModal()">
            取消
          </button>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn btn-sm hover:border-cyan-500"
              @click="copyGuide()"
            >
              📋 复制命令
            </button>
            <button
              type="button"
              class="btn btn-sm btn-primary flex items-center gap-1.5"
              @click="runInTerminal()"
            >
              <span>💻 在终端中执行</span>
              <span v-if="selectedTarget === 'host'" class="text-[10px] opacity-80">(宿主)</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
