import { defineComponent } from 'vue';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'WorkerInstallGuideModal',
  props: {
    show: { type: Boolean, default: false },
    worker: { type: Object, default: () => ({}) },
  },
  emits: ['close'],
  setup(props, { emit }) {
    function closeModal() {
      emit('close');
    }

    async function copyGuide(text) {
      if (!text) return;
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          await navigator.clipboard.writeText(text);
        } else {
          const ta = document.createElement('textarea');
          ta.value = text;
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
      closeModal,
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
            当前函数微服务声明了必需的语言运行时。请在宿主终端或系统控制台中执行以下安装命令后，刷新页面即可直接启动运行：
          </p>
        </div>

        <!-- 当前系统推荐安装命令 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span>当前系统推荐安装命令:</span>
            <button
              v-if="worker?.installGuideText"
              type="button"
              class="text-xs text-cyan-400 hover:text-cyan-300 underline"
              @click="copyGuide(worker.installGuideText)"
            >
              📋 一键复制命令
            </button>
          </div>
          <div class="p-3 bg-cyber-bg/90 rounded border border-cyber-line/60 font-mono text-xs text-amber-300 break-all select-all">
            {{ worker?.installGuideText || '暂未匹配到当前操作系统的特定安装指南，请参考官方文档手动安装。' }}
          </div>
        </div>

        <!-- 常用发行版快速参考 -->
        <div class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="text-xs font-semibold text-slate-300">各主流发行版快速参考:</div>
          <div class="space-y-1.5 max-h-48 overflow-y-auto text-[11px]">
            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Alpine Linux (Docker):</span>
                <button type="button" class="text-[10px] text-slate-400 hover:text-slate-200" @click="copyGuide('apk add --no-cache ' + (worker?.missingDependencies?.[0] || ''))">
                  复制
                </button>
              </div>
              <div class="font-mono text-slate-300">apk add --no-cache {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Debian / Ubuntu:</span>
                <button type="button" class="text-[10px] text-slate-400 hover:text-slate-200" @click="copyGuide('apt update && apt install -y ' + (worker?.missingDependencies?.[0] || ''))">
                  复制
                </button>
              </div>
              <div class="font-mono text-slate-300">apt update && apt install -y {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">RHEL / CentOS / Rocky:</span>
                <button type="button" class="text-[10px] text-slate-400 hover:text-slate-200" @click="copyGuide('dnf install -y ' + (worker?.missingDependencies?.[0] || ''))">
                  复制
                </button>
              </div>
              <div class="font-mono text-slate-300">dnf install -y {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>

            <div class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1">
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="text-cyan-400 font-semibold">Windows:</span>
                <button type="button" class="text-[10px] text-slate-400 hover:text-slate-200" @click="copyGuide('winget install ' + (worker?.missingDependencies?.[0] || ''))">
                  复制
                </button>
              </div>
              <div class="font-mono text-slate-300">winget install {{ worker?.missingDependencies?.[0] || 'runtime' }}</div>
            </div>
          </div>
        </div>

        <div class="flex justify-end pt-2 border-t border-cyber-line/50">
          <button type="button" class="btn btn-sm btn-primary" @click="closeModal()">我知道了</button>
        </div>
      </div>
    </div>
  `,
});
