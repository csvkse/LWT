import { defineComponent, onMounted, ref, watch } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { buildFileBreadcrumbs, inferExcludeRuleOptions } from '../utils/filePaths.js';

// 文件/文件夹选择器弹窗：浏览服务器目录并返回所选路径。
// mode='file' 可选中文件；mode='folder' 可选当前目录；mode='any' 可选文件或文件夹；mode='exclude' 连续点选排除规则。复用 /api/Files 列表。
export default defineComponent({
  name: 'FilePicker',
  props: {
    show: { type: Boolean, default: false },
    mode: { type: String, default: 'folder' }, // 'file' | 'folder' | 'any' | 'exclude'
    startPath: { type: String, default: '/' },
    basePath: { type: String, default: '' },
    title: { type: String, default: '' },
    target: { type: String, default: 'container' }, // 'container' | 'host'
    allowTargetSwitch: { type: Boolean, default: true },
  },
  emits: ['select', 'close', 'select-exclude'],
  setup(props, { emit }) {
    const currentPath = ref(props.startPath || '/');
    const currentTarget = ref(props.target || 'container');
    const isPrivilegedContainer = ref(false);
    const entries = ref([]);
    const loading = ref(false);
    const error = ref('');
    const selectedFile = ref(''); // mode='file' 时选中的文件路径
    const breadcrumbs = ref([]);
    const virtualRoot = ref(false);
    const excludeRuleModal = ref(null); // { entry, info, selectedPattern }

    async function load(targetPath = currentPath.value) {
      loading.value = true;
      error.value = '';
      try {
        const result = await http(API.files.list, {
          method: 'GET',
          params: { path: targetPath, target: currentTarget.value }
        });
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

    async function switchTarget(t) {
      if (currentTarget.value === t) return;
      currentTarget.value = t;
      currentPath.value = '/';
      await load('/');
    }

    watch(() => props.show, (val) => {
      if (val) {
        currentTarget.value = props.target || 'container';
        currentPath.value = props.startPath || '/';
        load(currentPath.value);
      }
    });

    watch(() => props.startPath, (val) => {
      if (val && props.show) {
        currentPath.value = val;
        load(val);
      }
    });

    watch(() => props.target, (val) => {
      if (val) {
        currentTarget.value = val;
      }
    });

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

    function pickDir(entry) {
      emit('select', entry.path, currentTarget.value);
      selectedFile.value = '';
      currentPath.value = props.startPath;
    }

    function openExcludeMenu(entry) {
      const info = inferExcludeRuleOptions(props.basePath || currentPath.value, entry);
      excludeRuleModal.value = {
        entry,
        info,
        selectedPattern: info.options[0]?.pattern || ''
      };
    }

    function confirmAddExclude(pattern) {
      const pat = pattern || excludeRuleModal.value?.selectedPattern;
      if (!pat) return;
      emit('select-exclude', pat);
      toast.success(`已添加排除规则: ${pat}`);
      excludeRuleModal.value = null;
    }

    function confirmSelect() {
      if (loading.value || error.value || (virtualRoot.value && !selectedFile.value)) return;
      if (props.mode === 'file') {
        if (!selectedFile.value) return toast.error('请选择一个文件');
        emit('select', selectedFile.value, currentTarget.value);
        return;
      }
      if (props.mode === 'any') {
        if (selectedFile.value) {
          emit('select', selectedFile.value, currentTarget.value);
        } else {
          emit('select', currentPath.value, currentTarget.value);
        }
        return;
      }
      if (props.mode === 'exclude') {
        close();
        return;
      }
      emit('select', currentPath.value, currentTarget.value);
    }

    function close() {
      currentPath.value = props.startPath || '/';
      selectedFile.value = '';
      excludeRuleModal.value = null;
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

    onMounted(async () => {
      try {
        const envRes = await http(API.services.environment, { method: 'GET' }).catch(() => null);
        if (envRes && envRes.ok && envRes.data) {
          isPrivilegedContainer.value = envRes.data.isPrivilegedContainer === true;
        }
      } catch {}

      if (props.show) load();
    });

    return {
      currentPath, currentTarget, isPrivilegedContainer, switchTarget,
      entries, loading, error, selectedFile, breadcrumbs, virtualRoot,
      excludeRuleModal,
      load, goPath, goUp, openEntry, confirmSelect, close, pickFile, pickDir,
      openExcludeMenu, confirmAddExclude, formatBytes, formatTime,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
      <div class="panel w-full max-w-2xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
        <div class="flex items-center gap-2 mb-3">
          <h3 class="font-display text-base text-neon-soft shrink-0">
            {{ title || (mode === 'file' ? '选择文件' : mode === 'any' ? '选择文件 / 文件夹' : mode === 'exclude' ? '点选目录/文件添加排除规则' : '选择文件夹') }}
          </h3>

          <!-- 仅在特权容器环境中显示目标环境切换 -->
          <div v-if="allowTargetSwitch && isPrivilegedContainer" class="flex items-center bg-slate-900 border border-slate-700/60 rounded p-0.5 text-xs ml-2">
            <button 
              type="button"
              class="px-2 py-0.5 rounded transition-colors flex items-center gap-1 font-medium"
              :class="currentTarget === 'container' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="switchTarget('container')"
            >
              <span>📦 容器</span>
            </button>
            <button 
              type="button"
              class="px-2 py-0.5 rounded transition-colors flex items-center gap-1 font-medium"
              :class="currentTarget === 'host' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="switchTarget('host')"
            >
              <span>🖥️ 宿主</span>
            </button>
          </div>

          <span class="text-xs text-slate-500 ml-2 truncate min-w-0 flex-1" :title="currentPath">
            <span v-if="currentTarget === 'host'" class="text-amber-400 font-mono mr-1">[宿主]</span>
            {{ currentPath }}
          </span>
          <button class="btn btn-xs ml-auto shrink-0" @click="close()">✕</button>
        </div>

        <div v-if="mode === 'exclude' && basePath" class="text-xs text-slate-400 bg-slate-900/50 px-2.5 py-1.5 rounded mb-2 flex items-center gap-2">
          <span class="text-slate-500">相对基准根路径:</span>
          <span class="font-mono text-cyan-300 truncate" :title="basePath">{{ basePath }}</span>
          <span class="text-slate-500 ml-auto shrink-0">(点击右侧 ＋ 排除 按钮生成规则)</span>
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
                    <span v-if="entry.isSymlink" class="badge !border-slate-600/40 text-slate-400 !text-[0.6rem] !py-0 !px-1 shrink-0">🔗</span>
                  </div>
                </td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ entry.isDirectory ? '—' : formatBytes(entry.size) }}</td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ formatTime(entry.modified) }}</td>
                <td class="text-right text-xs whitespace-nowrap">
                  <span v-if="mode === 'any' && entry.isDirectory" class="badge border-amber-500/50 text-amber-300 cursor-pointer"
                        @click.stop="pickDir(entry)">选文件夹</span>
                  <span v-if="(mode === 'file' || mode === 'any') && !entry.isDirectory" class="badge border-emerald-500/50 text-emerald-300 cursor-pointer"
                        @click.stop="pickFile(entry)">选择</span>
                  <button v-if="mode === 'exclude'" type="button"
                          class="badge border-rose-500/50 text-rose-300 hover:bg-rose-500/20 cursor-pointer transition px-2 py-0.5"
                          @click.stop="openExcludeMenu(entry)"
                          title="为此项添加排除过滤规则">
                    ＋ 排除
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-end gap-2 mt-4">
          <span v-if="(mode === 'file' || mode === 'any') && selectedFile" class="text-xs text-emerald-300 truncate min-w-0 flex-1 mr-2" :title="selectedFile">
            <span v-if="currentTarget === 'host'" class="text-amber-400 font-mono mr-1">[宿主]</span>{{ selectedFile }}
          </span>
          <span v-else-if="mode === 'exclude'" class="text-xs text-slate-400 truncate min-w-0 flex-1 mr-2">可连续点击行右侧「＋ 排除」添加多条规则</span>
          <button class="btn shrink-0" @click="close()">{{ mode === 'exclude' ? '完成' : '取消' }}</button>
          <button v-if="mode !== 'exclude'" class="btn btn-primary shrink-0"
                  :disabled="loading || !!error || (mode === 'file' && !selectedFile) || (virtualRoot && !selectedFile)"
                  @click="confirmSelect()">
            选择{{ mode === 'file' ? (selectedFile ? '' : '文件') : mode === 'any' ? (selectedFile ? '文件' : '当前文件夹') : '当前文件夹' }}
          </button>
        </div>
      </div>

      <!-- 智能排除规则选择弹出层 -->
      <div v-if="excludeRuleModal" class="fixed inset-0 z-[90] flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-md p-5 border border-rose-500/40 shadow-2xl" style="background: rgba(18, 24, 38, 0.98)">
          <div class="flex items-center gap-2 mb-3">
            <span class="text-rose-400 text-sm font-bold">🛡️ 添加排除过滤规则</span>
            <button class="btn btn-xs ml-auto" @click="excludeRuleModal = null">✕</button>
          </div>
          <div class="text-xs text-slate-400 mb-3">
            目标项: <span class="font-mono text-cyan-300 font-bold">{{ excludeRuleModal.entry.name }}</span>
            <div v-if="basePath" class="text-slate-500 text-[11px] mt-1 truncate">
              相对基准: {{ basePath }}
            </div>
          </div>
          <div class="space-y-2 mb-4 max-h-56 overflow-y-auto">
            <label v-for="opt in excludeRuleModal.info.options" :key="opt.pattern"
                   class="flex items-start gap-2.5 p-2.5 rounded border border-cyber-line/50 hover:border-cyan-500/50 cursor-pointer transition bg-slate-900/60"
                   :class="{ 'border-cyan-400 bg-cyan-950/20': excludeRuleModal.selectedPattern === opt.pattern }">
              <input type="radio" name="exclude_rule_option" :value="opt.pattern" v-model="excludeRuleModal.selectedPattern" class="mt-0.5" />
              <div class="min-w-0 flex-1">
                <div class="font-mono text-xs text-cyan-300 font-medium break-all">{{ opt.pattern }}</div>
                <div class="text-[11px] text-slate-400 mt-0.5">{{ opt.description }}</div>
              </div>
            </label>
          </div>
          <div class="flex justify-end gap-2">
            <button class="btn btn-xs" @click="excludeRuleModal = null">取消</button>
            <button class="btn btn-xs btn-primary" :disabled="!excludeRuleModal.selectedPattern"
                    @click="confirmAddExclude()">
              确认添加
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
