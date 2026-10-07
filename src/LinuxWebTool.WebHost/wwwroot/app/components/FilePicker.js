import { defineComponent, onMounted, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { buildFileBreadcrumbs } from '../utils/filePaths.js';

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
    const virtualRoot = ref(false);

    async function load(target = currentPath.value) {
      loading.value = true;
      error.value = '';
      try {
        const result = await http(API.files.list, { method: 'GET', params: { path: target } });
        if (result.ok && result.data && Array.isArray(result.data.entries)) {
          entries.value = result.data.entries;
          currentPath.value = result.data.path;
          virtualRoot.value = result.data.isVirtualRoot === true;
          breadcrumbs.value = buildFileBreadcrumbs(result.data.path);
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
      load(path);
    }

    function goUp() {
      const parts = breadcrumbs.value;
      if (parts.length > 1) goPath(parts[parts.length - 2].path);
    }

    function openEntry(entry) {
      if (entry.isDirectory) {
        selectedFile.value = '';
        goPath(entry.path);
      } else if (props.mode === 'file' || props.mode === 'any') {
        selectedFile.value = entry.path;
      }
    }

    function pickFile(entry) {
      selectedFile.value = entry.path;
    }

    // any 模式：直接把子文件夹作为所选路径返回。
    function pickDir(entry) {
      emit('select', entry.path);
      selectedFile.value = '';
      currentPath.value = props.startPath;
    }

    // any 模式：选中文件则返回文件；未选中文件时返回当前目录（作为文件夹路径）。
    function confirmSelect() {
      if (loading.value || error.value || virtualRoot.value && !selectedFile.value) return;
      if (props.mode === 'file') {
        if (!selectedFile.value) return toast.error('请选择一个文件');
        emit('select', selectedFile.value);
        return;
      }
      if (props.mode === 'any') {
        if (selectedFile.value) {
          emit('select', selectedFile.value);
        } else {
          emit('select', currentPath.value);
        }
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

    onMounted(() => load());

    return {
      currentPath, entries, loading, error, selectedFile, breadcrumbs, virtualRoot,
      load, goPath, goUp, openEntry, confirmSelect, close, pickFile, pickDir, formatBytes, formatTime,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
      <div class="panel w-full max-w-2xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
        <div class="flex items-center gap-2 mb-3">
          <h3 class="font-display text-base text-neon-soft shrink-0">
            {{ mode === 'file' ? '选择文件' : mode === 'any' ? '选择文件 / 文件夹' : '选择文件夹' }}
          </h3>
          <span class="text-xs text-slate-500 ml-2 truncate min-w-0 flex-1" :title="currentPath">{{ currentPath }}</span>
          <button class="btn btn-xs ml-auto shrink-0" @click="close()">✕</button>
        </div>

        <div class="flex items-center gap-2 text-xs text-slate-500 mb-2">
          <button class="btn btn-xs shrink-0" @click="goUp()">⬆ 上一级</button>
          <div class="flex items-center gap-2 overflow-x-auto no-scrollbar flex-1 min-w-0 py-0.5">
            <template v-for="(crumb, i) in breadcrumbs" :key="crumb.path">
              <span class="text-slate-600 shrink-0">/</span>
              <button class="hover:text-cyan-300 whitespace-nowrap shrink-0" @click="goPath(crumb.path)">{{ crumb.name }}</button>
            </template>
          </div>
          <button class="btn btn-xs ml-auto shrink-0" @click="load()">刷新</button>
        </div>

        <div v-if="error" class="text-rose-300 text-xs mb-2">{{ error }}</div>
        <div class="border border-cyber-line rounded-lg overflow-hidden flex-1 min-h-[16rem] overflow-y-auto">
          <table class="data-table">
            <thead>
              <tr>
                <th class="whitespace-nowrap">名称</th>
                <th class="whitespace-nowrap">大小</th>
                <th class="whitespace-nowrap">修改时间</th>
                <th class="text-right whitespace-nowrap">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-if="!entries.length && !loading"><td colspan="4" class="text-slate-600 py-8 text-center">{{ currentPath === '/' ? '根目录为空' : '此目录为空' }}</td></tr>
              <tr v-for="entry in entries" :key="entry.path" @click="openEntry(entry)"
                  class="cursor-pointer hover:bg-cyan-500/5 transition"
                  :class="{ 'bg-cyan-500/10': selectedFile === entry.path }">
                <td>
                  <div class="flex items-center gap-1.5 min-w-0">
                    <span class="shrink-0">{{ entry.isDirectory ? '📁' : '📄' }}</span>
                    <span class="font-mono text-xs truncate" :class="entry.isDirectory ? 'text-cyan-300/80' : 'text-slate-300'">{{ entry.name }}</span>
                  </div>
                </td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ entry.isDirectory ? '—' : formatBytes(entry.size) }}</td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ formatTime(entry.modified) }}</td>
                <td class="text-right text-xs whitespace-nowrap">
                  <span v-if="mode === 'any' && entry.isDirectory" class="badge border-amber-500/50 text-amber-300 cursor-pointer"
                        @click.stop="pickDir(entry)">选文件夹</span>
                  <span v-if="(mode === 'file' || mode === 'any') && !entry.isDirectory" class="badge border-emerald-500/50 text-emerald-300 cursor-pointer"
                        @click.stop="pickFile(entry)">选择</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-end gap-2 mt-4">
          <span v-if="(mode === 'file' || mode === 'any') && selectedFile" class="text-xs text-emerald-300 truncate min-w-0 flex-1 mr-2" :title="selectedFile">{{ selectedFile }}</span>
          <button class="btn shrink-0" @click="close()">取消</button>
          <button class="btn btn-primary shrink-0"
                  :disabled="loading || !!error || (mode === 'file' && !selectedFile) || (virtualRoot && !selectedFile)"
                  @click="confirmSelect()">
            选择{{ mode === 'file' ? (selectedFile ? '' : '文件') : mode === 'any' ? (selectedFile ? '文件' : '当前文件夹') : '当前文件夹' }}
          </button>
        </div>
      </div>
    </div>
  `,
});
