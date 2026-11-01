import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginKvDrawer',
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
    const loading = ref(false);
    const saving = ref(false);
    const kvList = ref([]);
    const keyInput = ref('');
    const valInput = ref('');

    watch(
      () => [props.show, props.plugin],
      ([show, p]) => {
        if (show && p) {
          keyInput.value = '';
          valInput.value = '';
          loadKv();
        }
      },
      { immediate: true }
    );

    async function loadKv() {
      if (!props.plugin) return;
      loading.value = true;
      try {
        const res = await http(API.plugins.kv(props.plugin.id), { method: 'GET' });
        if (res.ok && Array.isArray(res.data)) {
          kvList.value = res.data;
        } else {
          toast.error(res.message || '获取 KV 列表失败');
        }
      } catch (err) {
        toast.error('请求 KV 数据异常: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function handleSet() {
      if (!props.plugin || !keyInput.value.trim()) {
        toast.warning('请输入键名 (Key)');
        return;
      }
      saving.value = true;
      try {
        const res = await http(API.plugins.kv(props.plugin.id), {
          method: 'POST',
          body: {
            key: keyInput.value.trim(),
            value: valInput.value,
          },
        });
        if (res.ok) {
          toast.success(`键 [${keyInput.value.trim()}] 保存成功`);
          keyInput.value = '';
          valInput.value = '';
          await loadKv();
        } else {
          toast.error(res.message || '保存 KV 失败');
        }
      } catch (err) {
        toast.error('保存 KV 异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    async function handleDelete(key) {
      if (!props.plugin || !key) return;
      try {
        const res = await http(API.plugins.kvItem(props.plugin.id, key), {
          method: 'DELETE',
        });
        if (res.ok) {
          toast.info(`键 [${key}] 已删除`);
          await loadKv();
        } else {
          toast.error(res.message || '删除 KV 失败');
        }
      } catch (err) {
        toast.error('删除 KV 异常: ' + err.message);
      }
    }

    function editItem(item) {
      keyInput.value = item.key;
      valInput.value = item.value || '';
    }

    function handleClose() {
      emit('close');
    }

    return {
      loading,
      saving,
      kvList,
      keyInput,
      valInput,
      loadKv,
      handleSet,
      handleDelete,
      editItem,
      handleClose,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-end bg-slate-950/70 backdrop-blur-sm">
      <div class="panel w-full max-w-xl h-full p-0 flex flex-col border-l border-amber-500/40 rounded-none shadow-2xl">
        <!-- 抽屉头部 -->
        <div class="px-4 py-3 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between shrink-0">
          <div class="flex items-center gap-2 min-w-0">
            <span class="text-lg">🗄️</span>
            <div class="min-w-0">
              <div class="font-display font-semibold text-slate-100 text-sm truncate">
                KV 存储空间 - {{ plugin.name }}
              </div>
              <div class="text-[11px] text-slate-400 font-mono truncate">
                ID: {{ plugin.id }} (共 {{ kvList.length }} 个键值项)
              </div>
            </div>
          </div>
          <button
            type="button"
            class="btn-ghost text-xs p-1 text-slate-400 hover:text-white"
            @click="handleClose()"
          >
            ✕
          </button>
        </div>

        <!-- 新增 / 编辑输入区域 -->
        <div class="p-3 bg-slate-900/60 border-b border-cyber-line/50 space-y-2 shrink-0">
          <div class="text-xs font-semibold text-slate-300">写入 / 更新键值项:</div>
          <div class="grid grid-cols-3 gap-2">
            <input
              v-model="keyInput"
              type="text"
              class="input-base text-xs font-mono col-span-1"
              placeholder="键名 (Key)"
            />
            <input
              v-model="valInput"
              type="text"
              class="input-base text-xs font-mono col-span-2"
              placeholder="键值 (Value 字符串或 JSON)"
            />
          </div>
          <div class="flex justify-end">
            <button
              type="button"
              class="btn-primary text-xs px-3 py-1 flex items-center gap-1.5"
              :disabled="saving || !keyInput.trim()"
              @click="handleSet()"
            >
              <span>{{ saving ? '写入中...' : '保存 KV 项' }}</span>
            </button>
          </div>
        </div>

        <!-- 键值项列表 -->
        <div class="flex-1 p-3 bg-slate-950 overflow-y-auto space-y-2 select-text">
          <div v-if="loading && kvList.length === 0" class="text-slate-500 italic py-4 text-center text-xs">
            加载 KV 数据中...
          </div>
          <div v-else-if="kvList.length === 0" class="text-slate-600 italic py-4 text-center text-xs">
            暂无存储的键值数据
          </div>
          <div
            v-for="item in kvList"
            :key="item.key"
            class="p-2.5 rounded bg-slate-900/70 border border-cyber-line/40 flex items-start justify-between gap-3 text-xs"
          >
            <div class="flex-1 min-w-0 font-mono">
              <div class="text-amber-300 font-semibold truncate">{{ item.key }}</div>
              <div class="text-slate-300 break-all mt-0.5 text-[11px] bg-slate-950/80 p-1.5 rounded border border-cyber-line/30">
                {{ item.value }}
              </div>
            </div>
            <div class="flex items-center gap-1 shrink-0 pt-0.5">
              <button
                type="button"
                class="btn-ghost p-1 text-slate-400 hover:text-cyan-300"
                title="编辑此项"
                @click="editItem(item)"
              >
                ✏️
              </button>
              <button
                type="button"
                class="btn-ghost p-1 text-slate-400 hover:text-red-400"
                title="删除此项"
                @click="handleDelete(item.key)"
              >
                🗑️
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
