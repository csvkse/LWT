import { defineComponent } from 'vue';

export default defineComponent({
  name: 'PluginBinaryFileCard',
  props: {
    activeFile: {
      type: String,
      required: true,
    },
    fileSizeBytes: {
      type: Number,
      default: 0,
    },
    uploading: {
      type: Boolean,
      default: false,
    },
  },
  emits: ['download', 'replace'],
  setup() {
    function formatSize(bytes) {
      if (!bytes || bytes <= 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return (bytes / Math.pow(k, i)).toFixed(i === 0 ? 0 : 1) + ' ' + sizes[i];
    }

    return {
      formatSize,
    };
  },
  template: `
    <div class="flex-1 flex flex-col items-center justify-center p-8 bg-slate-950/40 text-center">
      <div class="panel max-w-lg w-full p-6 border border-cyan-500/30 bg-slate-900/70 shadow-xl space-y-4">
        <div class="text-4xl text-cyan-400">⚙️</div>
        <div>
          <h3 class="text-base font-bold text-slate-100 font-mono break-all">{{ activeFile }}</h3>
          <div class="text-xs text-slate-400 mt-1">
            大小: <span class="text-cyan-300 font-mono">{{ formatSize(fileSizeBytes) }}</span> ({{ fileSizeBytes.toLocaleString() }} 字节)
          </div>
        </div>

        <div class="p-3 rounded bg-slate-950/80 border border-slate-800 text-left text-xs text-slate-300 space-y-1.5 leading-relaxed">
          <div class="flex items-center gap-1.5 text-cyan-300 font-medium">
            <span>💡</span>
            <span>动态热补丁说明：</span>
          </div>
          <div>• 此文件为可执行二进制程序或动态链接库，系统已自动赋予 Linux 可执行权限 (0755)。</div>
          <div>• 您可以通过【上传替换】直接更新插件核心二进制，<strong>无需重新构建 Docker 镜像或重建容器</strong>。</div>
          <div>• 替换后仅需在插件列表点击【🔄 重启】即可立即生效最新编译的二进制程序。</div>
        </div>

        <div class="flex items-center justify-center gap-3 pt-2">
          <button
            type="button"
            class="btn-secondary text-xs px-4 py-2 flex items-center gap-2"
            @click="$emit('download')"
          >
            <span>⬇️</span>
            <span>下载二进制文件</span>
          </button>
          <button
            type="button"
            class="btn-primary text-xs px-4 py-2 flex items-center gap-2 shadow-lg shadow-cyan-500/10"
            :disabled="uploading"
            @click="$emit('replace')"
          >
            <span>⬆️</span>
            <span>{{ uploading ? '上传替换中...' : '上传替换 (热更)' }}</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
