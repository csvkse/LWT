import { computed, defineComponent, ref } from 'vue';
import { API } from '../../config.js';
import { auth } from '../../store/auth.js';

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
      let url = '';
      const rawEntry = props.plugin.manifest?.ui?.entry || props.plugin.manifest?.ui?.entryFile;
      if (rawEntry && rawEntry !== 'web') {
        const cleanEntry = rawEntry.replace(/^\/+/, '').replace(/^ui\//, '');
        url = API.plugins.asset(props.plugin.id, cleanEntry);
      } else if (props.plugin.manifest?.ui?.entry === 'web' || props.plugin.gatewayUrl || props.plugin.manifest?.network?.mode === 'path') {
        url = props.plugin.gatewayUrl || API.plugins.gatewayUrl(props.plugin.id);
      } else {
        url = API.plugins.asset(props.plugin.id, 'index.html');
      }

      if (url && auth.token) {
        const sep = url.includes('?') ? '&' : '?';
        return `${url}${sep}token=${encodeURIComponent(auth.token)}`;
      }
      return url;
    });

    const displayUrl = computed(() => {
      if (!assetUrl.value) return '';
      return assetUrl.value.replace(/([?&]token=)[^&]+/, '$1***');
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

    function handleIframeLoad() {
      try {
        if (iframeRef.value?.contentWindow && props.plugin) {
          iframeRef.value.contentWindow.postMessage({
            type: 'LWT_PLUGIN_ENV',
            pluginId: props.plugin.id,
            allocatedPort: props.plugin.allocatedPort,
            configuredPort: props.plugin.configuredPort || props.plugin.manifest?.network?.port,
            isPortAvoided: Boolean(props.plugin.isPortAvoided),
            portConflictReason: props.plugin.portConflictReason,
            token: auth.token || '',
            hostname: window.location.hostname || '127.0.0.1'
          }, '*');
        }
      } catch {
        // Cross-origin fallback
      }
    }

    return {
      iframeRef,
      iframeKey,
      assetUrl,
      displayUrl,
      handleClose,
      handleReload,
      handleOpenTab,
      handleIframeLoad,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-5xl h-[88vh] p-0 flex flex-col border border-cyan-500/40 overflow-hidden shadow-2xl">
        <!-- 头部导航与操作栏 (支持防变形与 Token 安全脱敏) -->
        <div class="px-4 py-2.5 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between gap-3 shrink-0">
          <div class="flex items-center gap-2.5 min-w-0 flex-1">
            <span class="text-lg shrink-0">🖥️</span>
            <div class="min-w-0 flex-1">
              <div class="font-display font-semibold text-slate-100 text-sm truncate">
                {{ plugin.name }} - Webview 视图
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate max-w-[360px] sm:max-w-lg md:max-w-xl" :title="displayUrl">
                {{ displayUrl }}
              </div>
            </div>
          </div>

          <div class="flex items-center gap-1.5 sm:gap-2 shrink-0 flex-nowrap">
            <button
              type="button"
              class="btn-ghost text-xs px-2 sm:px-2.5 py-1 text-slate-300 hover:text-white flex items-center gap-1 shrink-0 whitespace-nowrap"
              title="重新加载页面"
              @click="handleReload()"
            >
              <span class="shrink-0">🔄</span>
              <span class="shrink-0 whitespace-nowrap">刷新</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs px-2 sm:px-2.5 py-1 text-slate-300 hover:text-cyan-300 flex items-center gap-1 shrink-0 whitespace-nowrap"
              title="在新标签页中打开"
              @click="handleOpenTab()"
            >
              <span class="shrink-0">↗</span>
              <span class="shrink-0 whitespace-nowrap">新标签页</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white shrink-0"
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
            sandbox="allow-scripts allow-forms allow-same-origin allow-downloads allow-modals"
            @load="handleIframeLoad()"
          ></iframe>
        </div>
      </div>
    </div>
  `,
});
