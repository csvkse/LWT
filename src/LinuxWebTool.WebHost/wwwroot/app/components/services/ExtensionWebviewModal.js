import { computed, defineComponent, onMounted, onUnmounted, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionWebviewModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: () => null },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const iframeKey = ref(0);

    const iframeSrc = computed(() => {
      if (!props.ext?.manifest?.id) return '';
      const entry = props.ext.manifest?.ui?.entry || 'index.html';
      return `/api${API.services.extensionAsset(props.ext.manifest.id, entry)}`;
    });

    const title = computed(() => {
      return props.ext?.manifest?.ui?.title || props.ext?.manifest?.name || '扩展控制台';
    });

    const icon = computed(() => {
      return props.ext?.manifest?.ui?.icon || props.ext?.manifest?.icon || '🖥️';
    });

    async function handleExtensionMessage(event) {
      if (!props.show || !props.ext) return;
      const data = event.data;
      if (!data || data.type !== 'LWT_EXTENSION_ACTION_REQUEST') return;
      if (data.extensionId && data.extensionId !== props.ext.manifest?.id) return;

      try {
        const payload = {
          extensionId: props.ext.manifest.id,
          action: data.action,
          ...(data.payload || {}),
        };
        const res = await http(API.services.extensionAction, {
          method: 'POST',
          body: payload,
        });

        if (event.source) {
          event.source.postMessage({
            type: 'LWT_EXTENSION_ACTION_RESPONSE',
            id: data.id,
            ok: res.ok && (res.data?.success !== false),
            data: res.data?.data !== undefined ? res.data.data : res.data,
            error: res.ok ? null : (res.data?.message || res.message || '操作执行失败'),
          }, '*');
        }
      } catch (err) {
        if (event.source) {
          event.source.postMessage({
            type: 'LWT_EXTENSION_ACTION_RESPONSE',
            id: data.id,
            ok: false,
            error: err.message || '请求处理异常',
          }, '*');
        }
      }
    }

    function refreshIframe() {
      iframeKey.value++;
      toast.info('正在刷新扩展控制台');
    }

    function openNewTab() {
      if (iframeSrc.value) {
        window.open(iframeSrc.value, '_blank');
      }
    }

    function closeModal() {
      emit('close');
    }

    onMounted(() => {
      window.addEventListener('message', handleExtensionMessage);
    });

    onUnmounted(() => {
      window.removeEventListener('message', handleExtensionMessage);
    });

    return {
      iframeKey,
      iframeSrc,
      title,
      icon,
      refreshIframe,
      openNewTab,
      closeModal,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-2 md:p-6 bg-black/80 backdrop-blur-sm animate-fade-in">
      <div class="panel w-full max-w-6xl h-[90vh] flex flex-col p-4 border-cyan-500/50 shadow-2xl overflow-hidden">
        <!-- 模态框顶栏 -->
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3 mb-3">
          <div class="flex items-center gap-2.5 min-w-0">
            <span class="text-xl">{{ icon }}</span>
            <div class="flex items-center gap-2 min-w-0">
              <span class="font-display font-semibold text-slate-100 text-sm truncate">{{ title }}</span>
              <span class="badge border-cyan-500/40 text-cyan-300 text-[11px] shrink-0 font-mono">
                v{{ ext?.manifest?.version || '1.0' }}
              </span>
              <span class="badge border-emerald-500/40 text-emerald-300 text-[10px] shrink-0">
                UI 扩展 (无HTTP)
              </span>
            </div>
          </div>
          <div class="flex items-center gap-2">
            <button type="button" class="btn btn-xs hover:border-cyan-500/40 text-slate-300" title="刷新视图" @click="refreshIframe()">
              🔄 刷新
            </button>
            <button type="button" class="btn btn-xs hover:border-cyan-500/40 text-cyan-400" title="在新标签页中打开" @click="openNewTab()">
              ↗ 新窗口
            </button>
            <button type="button" class="btn btn-xs hover:border-slate-500 text-slate-400" title="关闭" @click="closeModal()">
              ✕
            </button>
          </div>
        </div>

        <!-- 嵌入式 Webview 沙箱 Iframe -->
        <div class="flex-1 w-full min-h-0 bg-cyber-bg/90 rounded border border-cyber-line/40 overflow-hidden relative">
          <iframe
            v-if="iframeSrc"
            :key="iframeKey"
            :src="iframeSrc"
            class="w-full h-full border-0"
            allow="clipboard-read; clipboard-write"
          ></iframe>
          <div v-else class="w-full h-full flex items-center justify-center text-slate-500 text-xs">
            暂无可用 UI 页面
          </div>
        </div>
      </div>
    </div>
  `,
});
