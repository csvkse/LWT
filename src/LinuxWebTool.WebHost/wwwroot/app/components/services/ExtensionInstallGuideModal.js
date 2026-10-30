import { defineComponent } from 'vue';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionInstallGuideModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: () => ({}) },
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
            当前扩展包声明了必需的环境依赖。请在宿主终端或系统控制台中执行以下安装命令后，点击「重新扫描插件」即可启用：
          </p>
        </div>

        <!-- 推荐安装命令 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between text-xs text-slate-400">
            <span>当前系统推荐安装命令:</span>
            <button
              v-if="ext?.installGuideText"
              type="button"
              class="text-xs text-cyan-400 hover:text-cyan-300 underline"
              @click="copyGuide(ext.installGuideText)"
            >
              📋 一键复制命令
            </button>
          </div>
          <div class="p-3 bg-cyber-bg/90 rounded border border-cyber-line/60 font-mono text-xs text-amber-300 break-all select-all">
            {{ ext?.installGuideText || '暂未匹配到当前操作系统的特定安装指南，请参考官方文档手动安装。' }}
          </div>
        </div>

        <!-- 多发行版参考清单 -->
        <div v-if="ext?.manifest?.installGuide && Object.keys(ext.manifest.installGuide).length > 0" class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="text-xs font-semibold text-slate-300">其他 Linux 发行版参考:</div>
          <div class="space-y-1.5 max-h-48 overflow-y-auto">
            <div
              v-for="(cmd, distro) in ext.manifest.installGuide"
              :key="distro"
              class="p-2 rounded bg-slate-900/60 border border-slate-700/50 flex flex-col gap-1 text-[11px]"
            >
              <div class="flex items-center justify-between text-slate-400 font-mono">
                <span class="capitalize text-cyan-400 font-semibold">{{ distro }}:</span>
                <button type="button" class="text-[10px] text-slate-400 hover:text-slate-200" @click="copyGuide(cmd)">
                  复制
                </button>
              </div>
              <code class="text-slate-300 font-mono text-[10px] break-all select-all">{{ cmd }}</code>
            </div>
          </div>
        </div>

        <div class="flex justify-end pt-2">
          <button type="button" class="btn btn-sm btn-primary" @click="closeModal()">
            我知道了
          </button>
        </div>
      </div>
    </div>
  `,
});
