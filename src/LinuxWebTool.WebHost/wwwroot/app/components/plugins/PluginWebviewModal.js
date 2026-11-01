import { computed, defineComponent, ref } from 'vue';
import { API } from '../../config.js';

export default defineComponent({
  name: 'PluginWebviewModal',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
    plugin: {
      type: Object,
      default: null,
    },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const iframeRef = ref(null);
    const iframeKey = ref(0);

    const assetUrl = computed(() => {
      if (!props.plugin) return '';
      const entry = props.plugin.manifest?.ui?.entryFile || 'index.html';
      return API.plugins.asset(props.plugin.id, entry);
    });

    function handleClose() {
      emit('close');
    }

    function handleReload() {
      iframeKey.value++;
    }

    function handleOpenTab() {
      if (assetUrl.value) {
        window.open(assetUrl.value, '_blank');
      }
    }

    return {
      iframeRef,
      iframeKey,
      assetUrl,
      handleClose,
      handleReload,
      handleOpenTab,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-5xl h-[88vh] p-0 flex flex-col border border-cyan-500/40 overflow-hidden shadow-2xl">
        <!-- 头部导航与操作栏 -->
        <div class="px-4 py-2.5 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between shrink-0">
          <div class="flex items-center gap-2 min-w-0">
            <span class="text-lg">🖥️</span>
            <div class="min-w-0">
              <div class="font-display font-semibold text-slate-100 text-sm truncate">
                {{ plugin.name }} - Webview 视图
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate">
                {{ assetUrl }}
              </div>
            </div>
          </div>

          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn-ghost text-xs px-2.5 py-1 text-slate-300 hover:text-white flex items-center gap-1"
              title="重新加载页面"
              @click="handleReload()"
            >
              <span>🔄</span>
              <span>刷新</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs px-2.5 py-1 text-slate-300 hover:text-cyan-300 flex items-center gap-1"
              title="在新标签页中打开"
              @click="handleOpenTab()"
            >
              <span>↗</span>
              <span>新标签页</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white"
              title="关闭"
              @click="handleClose()"
            >
              ✕
            </button>
          </div>
        </div>

        <!-- Webview 沙箱 IFrame -->
        <div class="flex-1 w-full bg-slate-950 relative">
          <iframe
            :key="iframeKey"
            ref="iframeRef"
            :src="assetUrl"
            class="w-full h-full border-0"
            sandbox="allow-scripts allow-forms allow-same-origin allow-downloads"
          ></iframe>
        </div>
      </div>
    </div>
  `,
});
