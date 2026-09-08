import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

// 文件/文件夹选择器弹窗：浏览服务器目录并返回所选路径。
// mode='file' 可选中文件；mode='folder' 可选当前目录。复用 /api/Files 列表。
export default defineComponent({
  name: 'FilePicker',
  props: {
    show: { type: Boolean, default: false },
    mode: { type: String, default: 'folder' }, // 'file' | 'folder'
    startPath: { type: String, default: '/' },
  },
  emits: ['select', 'close'],
  setup(props, { emit }) {
    const currentPath = ref(props.startPath);
    const entries = ref([]);
    const loading = ref(false);
    const error = ref('');
    const selectedFile = ref(''); // mode='file' 时选中的文件路径
    const breadcrumbs = ref([]);

    function buildBreadcrumbs(path) {
      const parts = [];
      let cur = path;
      while (cur && cur !== '/') {
        const idx = cur.lastIndexOf('/');
        const name = idx <= 0 ? cur : cur.slice(idx + 1);
        parts.unshift({ name, path: cur });
        cur = idx <= 0 ? '/' : cur.slice(0, idx);
      }
      parts.unshift({ name: '/', path: '/' });
      return parts;
    }

    async function load() {
      loading.value = true;
      error.value = '';
      try {
        const result = await http(API.files.list, { params: { path: currentPath.value } });
        if (result.ok && result.data && Array.isArray(result.data.entries)) {
          entries.value = result.data.entries;
          breadcrumbs.value = buildBreadcrumbs(result.data.path);
          selectedFile.value = '';
          return;
        }
        error.value = result.message || '目录加载失败';
      } catch (e) {
        error.value = e?.message || '目录加载失败';
      } finally {
        loading.value = false;
      }
    }

    function goPath(path) {
      currentPath.value = path;
      load();
    }

    function goUp() {
      const parts = breadcrumbs.value;
      if (parts.length > 1) goPath(parts[parts.length - 2].path);
    }

    function openEntry(entry) {
      if (entry.isDirectory) {
        selectedFile.value = '';
        goPath(entry.path);
      } else if (props.mode === 'file') {
        selectedFile.value = entry.path;
      }
    }

    function pickFile(entry) {
      selectedFile.value = entry.path;
    }

    function confirmSelect() {
      if (props.mode === 'file') {
        if (!selectedFile.value) return toast.error('请选择一个文件');
        emit('select', selectedFile.value);
        return;
      }
      // folder 模式：选当前目录
      emit('select', currentPath.value);
    }

    function close() {
      currentPath.value = props.startPath;
      selectedFile.value = '';
      emit('close');
    }

    function formatBytes(n) {
      if (!n) return '—';
      if (n < 1024) return n + ' B';
      if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
      if (n < 1024 * 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + ' MB';
      return (n / 1024 / 1024 / 1024).toFixed(2) + ' GB';
    }

    function formatTime(t) {
      if (!t) return '';
      const d = new Date(t);
      const pad = (x) => String(x).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    onMounted(load);

    return {
      currentPath, entries, loading, error, selectedFile, breadcrumbs,
      load, goPath, goUp, openEntry, confirmSelect, close, pickFile, formatBytes, formatTime,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
      <div class="panel w-full max-w-2xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
        <div class="flex items-center gap-2 mb-3">
          <h3 class="font-display text-base text-neon-soft">{{ mode === 'file' ? '选择文件' : '选择文件夹' }}</h3>
          <span class="text-xs text-slate-500 ml-2">{{ currentPath }}</span>
          <button class="btn btn-xs ml-auto" @click="close()">✕</button>
        </div>

        <div class="flex items-center gap-2 text-xs text-slate-500 mb-2 overflow-x-auto no-scrollbar">
          <button class="btn btn-xs" @click="goUp()">⬆ 上一级</button>
          <template v-for="(crumb, i) in breadcrumbs" :key="crumb.path">
            <span class="text-slate-600">/</span>
            <button class="hover:text-cyan-300" @click="goPath(crumb.path)">{{ crumb.name }}</button>
          </template>
          <button class="btn btn-xs ml-auto" @click="load()">刷新</button>
        </div>

        <div v-if="error" class="text-rose-300 text-xs mb-2">{{ error }}</div>
        <div class="border border-cyber-line rounded-lg overflow-hidden flex-1 min-h-[16rem] overflow-y-auto">
          <table class="data-table">
            <thead>
              <tr><th>名称</th><th>大小</th><th>修改时间</th><th class="text-right">操作</th></tr>
            </thead>
            <tbody>
              <tr v-if="!entries.length && !loading"><td colspan="4" class="text-slate-600 py-8 text-center">{{ currentPath === '/' ? '根目录为空' : '此目录为空' }}</td></tr>
              <tr v-for="entry in entries" :key="entry.path" @click="openEntry(entry)"
                  class="cursor-pointer hover:bg-cyan-500/5 transition"
                  :class="{ 'bg-cyan-500/10': selectedFile === entry.path }">
                <td>
                  <span class="mr-1.5">{{ entry.isDirectory ? '📁' : '📄' }}</span>
                  <span class="font-mono text-xs" :class="entry.isDirectory ? 'text-cyan-300/80' : 'text-slate-300'">{{ entry.name }}</span>
                </td>
                <td class="text-xs text-slate-500">{{ entry.isDirectory ? '—' : formatBytes(entry.size) }}</td>
                <td class="text-xs text-slate-500">{{ formatTime(entry.modified) }}</td>
                <td class="text-right text-xs">
                  <span v-if="mode === 'file' && !entry.isDirectory" class="badge border-emerald-500/50 text-emerald-300"
                        @click.stop="pickFile(entry)">选择</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-end gap-2 mt-4">
          <span v-if="mode === 'file' && selectedFile" class="text-xs text-emerald-300 truncate mr-auto">{{ selectedFile }}</span>
          <button class="btn" @click="close()">取消</button>
          <button class="btn btn-primary" :disabled="mode === 'file' && !selectedFile" @click="confirmSelect()">
            选择{{ mode === 'file' ? (selectedFile ? '' : '文件') : '当前文件夹' }}
          </button>
        </div>
      </div>
    </div>
  `,
});
