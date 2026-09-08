import { computed, defineComponent, onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { http, httpUpload } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';

export default defineComponent({
  name: 'FilesView',
  setup() {
    const route = useRoute();
    const router = useRouter();
    const currentPath = ref('/');
    const entries = ref([]);
    const breadcrumbs = ref([]);
    const isRoot = ref(true);
    const loading = ref(false);
    const loadError = ref('');

    // 文本编辑器
    const showEditor = ref(false);
    const editorPath = ref('');
    const editorName = ref('');
    const editorContent = ref('');
    const editorLoading = ref(false);
    const editorSaving = ref(false);

    // 新建文件夹
    const showMkdir = ref(false);
    const mkdirName = ref('');

    // 重命名
    const showRename = ref(false);
    const renameName = ref('');
    const renamePath = ref('');

    // 上传
    const uploadInput = ref(null);
    const uploadPath = ref('');

    // 当前目录信息
    const dirName = computed(() => currentPath.value === '/' ? '/' : currentPath.value.split('/').pop());

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

    async function load(path) {
      const target = path ?? currentPath.value;
      loading.value = true;
      loadError.value = '';
      try {
        const result = await http(API.files.list, { params: { path: target } });
        if (result.ok && result.data && Array.isArray(result.data.entries)) {
          currentPath.value = result.data.path;
          entries.value = result.data.entries;
          breadcrumbs.value = buildBreadcrumbs(result.data.path);
          isRoot.value = result.data.isRoot === true;
          return;
        }
        loadError.value = result.message || '目录加载失败';
        if (result && result.status === 404) {
          // 路径不存在时回退到上一层
          goUp();
        }
      } catch (e) {
        loadError.value = e?.message || '目录加载失败';
      } finally {
        loading.value = false;
      }
    }

    function goPath(path) {
      load(path);
      router.replace({ path: '/files', query: { path } });
    }

    function goUp() {
      const parts = breadcrumbs.value;
      if (parts.length > 1) goPath(parts[parts.length - 2].path);
    }

    function formatBytes(n) {
      if (!n || n <= 0) return '—';
      const units = ['B', 'KB', 'MB', 'GB', 'TB'];
      let v = n, i = 0;
      while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
      return v.toFixed(i === 0 ? 0 : 1) + ' ' + units[i];
    }

    function formatTime(t) {
      if (!t) return '';
      const d = new Date(t);
      const pad = (x) => String(x).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    function iconFor(entry) {
      if (entry.isDirectory) return '📁';
      const ext = (entry.extension || '').toLowerCase();
      if (['.txt', '.md', '.log', '.json', '.yaml', '.yml', '.cs', '.js', '.ts', '.py', '.sh', '.rs', '.go', '.xml', '.html', '.css', '.ini', '.conf'].includes(ext)) return '📄';
      if (['.mp4', '.mkv', '.mov', '.avi', '.mp3', '.flac', '.wav', '.jpg', '.png', '.gif', '.webp'].includes(ext)) return '🎞';
      return '📄';
    }

    // 打开目录 / 文本文件
    function openEntry(entry) {
      if (entry.isDirectory) {
        goPath(entry.path);
      } else if (entry.isTextual) {
        openEditor(entry.path);
      } else {
        toast.info('该文件非文本，暂不支持在线编辑');
      }
    }

    async function openEditor(path) {
      showEditor.value = true;
      editorPath.value = path;
      editorName.value = path.split('/').pop();
      editorContent.value = '';
      editorLoading.value = true;
      try {
        const result = await http(API.files.content, { params: { path } });
        if (result.ok && result.data) {
          editorContent.value = result.data.content ?? '';
        } else if (result.ok && result.data.tooLarge) {
          toast.error('文件过大，无法在线编辑');
          showEditor.value = false;
        } else if (result.ok && result.data.binary) {
          toast.error('二进制文件，无法在线编辑');
          showEditor.value = false;
        } else {
          toast.error(result.message || '读取失败');
          showEditor.value = false;
        }
      } catch (e) {
        toast.error(e?.message || '读取失败');
        showEditor.value = false;
      } finally {
        editorLoading.value = false;
      }
    }

    async function saveEditor() {
      editorSaving.value = true;
      try {
        const result = await http(API.files.content, {
          method: 'POST',
          body: { path: editorPath.value, content: editorContent.value },
        });
        if (result.ok) {
          toast.success('已保存');
          showEditor.value = false;
          await load();
        }
      } finally {
        editorSaving.value = false;
      }
    }

    // 新建文件夹
    function openMkdir() {
      mkdirName.value = '';
      showMkdir.value = true;
    }

    async function createMkdir() {
      const name = mkdirName.value.trim();
      if (!name) return toast.error('请输入文件夹名称');
      mkdirName.value = name;
      const newPath = currentPath.value === '/' ? `/${name}` : `${currentPath.value}/${name}`;
      try {
        const result = await http(API.files.mkdir, { method: 'POST', body: { path: newPath } });
        if (result.ok) {
          toast.success('文件夹已创建');
          showMkdir.value = false;
          await load();
        }
      } finally {
        mkdirName.value = '';
      }
    }

    // 重命名
    function renameEntry(entry) {
      renameName.value = entry.name;
      renamePath.value = entry.path;
      showRename.value = true;
    }

    async function saveRename() {
      const newName = renameName.value.trim();
      if (!newName) return toast.error('请输入新名称');
      if (newName === renamePath.value.split('/').pop()) { showRename.value = false; return; }
      const parent = renamePath.value.slice(0, renamePath.value.lastIndexOf('/')) || '';
      const newPath = parent === '' ? `/${newName}` : `${parent}/${newName}`;
      try {
        const result = await http(API.files.rename, {
          method: 'POST',
          body: { from: renamePath.value, to: newPath },
        });
        if (result.ok) {
          toast.success('已重命名');
          showRename.value = false;
          await load();
        }
      } finally {
        showRename.value = false;
      }
    }

    // 删除（文件直接删；目录非空需递归确认）
    function removeEntry(entry) {
      const isDir = entry.isDirectory;
      openConfirm({
        title: `删除${isDir ? '文件夹' : '文件'}`,
        message: `确定删除「${entry.name}」？此操作不可撤销。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.files.remove(), { params: { path: entry.path } });
          if (result.ok) {
            toast.success('已删除');
            await load();
          } else if (result && result.data && result.data.needRecursive) {
            // 目录非空，需递归删除
            openConfirm({
              title: '递归删除文件夹',
              message: `「${entry.name}」非空，继续将删除其中所有内容。继续？`,
              confirmText: '强制删除',
              danger: true,
              onConfirm: async () => {
                const r2 = await http(API.files.remove(), { params: { path: entry.path, recursive: true } });
                if (r2.ok) {
                  toast.success('已删除');
                  await load();
                }
              },
            });
          }
        },
      });
    }

    // 上传
    function triggerUpload() {
      uploadPath.value = currentPath.value;
      if (uploadInput.value) uploadInput.value.click();
    }

    async function onUpload(e) {
      const file = e.target.files && e.target.files[0];
      e.target.value = '';
      if (!file) return;
      try {
        const result = await httpUpload(API.files.upload(), { params: { path: uploadPath.value }, file });
        if (result.ok) {
          toast.success('上传成功');
          await load();
        }
      } catch {
        toast.error('上传失败');
      }
    }

    function goHome() {
      goPath('/');
    }

    onMounted(() => {
      const q = route.query.path;
      const start = typeof q === 'string' && q ? q : '/';
      currentPath.value = start;
      load(start);
    });

    return {
      currentPath, entries, breadcrumbs, isRoot, loading, loadError, dirName,
      showEditor, editorPath, editorName, editorContent, editorLoading, editorSaving,
      showMkdir, mkdirName, showRename, renameName, renamePath, uploadInput,
      load, goPath, goUp, goHome, formatBytes, formatTime, iconFor, openEntry,
      openEditor, saveEditor, openMkdir, createMkdir, renameEntry, saveRename, removeEntry,
      triggerUpload, onUpload,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-center gap-2">
        <h2 class="text-sm text-slate-400">文件管理器</h2>
        <div class="ml-auto flex gap-1">
          <button class="btn btn-xs" @click="openMkdir()">＋ 新建文件夹</button>
          <button class="btn btn-xs" @click="triggerUpload()">⬆ 上传</button>
        </div>
      </div>

      <div class="panel p-3 flex items-center gap-2 flex-wrap">
        <button class="btn btn-xs" @click="goHome()">🏠</button>
        <button class="btn btn-xs" @click="goUp()" :disabled="isRoot">⬆ 上一级</button>
        <div class="text-xs text-cyan-300/80 font-mono truncate flex-1 min-w-0 overflow-x-auto no-scrollbar">
          <template v-for="(crumb, i) in breadcrumbs" :key="crumb.path">
            <span class="text-slate-600">/</span>
            <button class="hover:text-cyan-300" @click="goPath(crumb.path)">{{ crumb.name }}</button>
          </template>
        </div>
        <button class="btn btn-xs" @click="load()">刷新</button>
      </div>

      <div v-if="loadError" class="panel !border-rose-500/40 bg-rose-500/5 text-rose-200/90 text-xs px-4 py-3 flex items-center gap-3">
        <span>{{ loadError }}</span>
        <button class="btn btn-xs ml-auto" @click="load()">重试</button>
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[48rem]">
          <thead>
            <tr><th>名称</th><th>大小</th><th>修改时间</th><th class="text-right">操作</th></tr>
          </thead>
          <tbody>
            <tr v-if="!entries.length && !loading"><td colspan="4" class="text-slate-600 py-8 text-center">{{ dirName === '/' ? '根目录为空' : '此目录为空' }}</td></tr>
            <tr v-for="entry in entries" :key="entry.path" class="hover:bg-cyan-500/5 transition cursor-pointer" @click="openEntry(entry)">
              <td>
                <span class="mr-1.5">{{ iconFor(entry) }}</span>
                <span class="font-mono text-xs" :class="entry.isDirectory ? 'text-cyan-300/80' : 'text-slate-300'">{{ entry.name }}</span>
              </td>
              <td class="text-xs text-slate-500">{{ entry.isDirectory ? '—' : formatBytes(entry.size) }}</td>
              <td class="text-xs text-slate-500">{{ formatTime(entry.modified) }}</td>
              <td class="text-right whitespace-nowrap text-xs" @click.stop>
                <button class="btn btn-xs" @click="renameEntry(entry)">重命名</button>
                <button class="btn btn-xs btn-danger" @click="removeEntry(entry)">删除</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 文本编辑弹窗 -->
      <div v-if="showEditor" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3">
            <h3 class="font-display text-base text-neon-soft">编辑文本 · {{ editorName }}</h3>
            <span class="text-xs text-slate-500 truncate ml-auto">{{ editorPath }}</span>
            <button class="btn btn-xs" @click="showEditor = false">✕</button>
          </div>
          <textarea class="input font-mono !text-[0.8rem] flex-1 min-h-[24rem] resize-y" v-model="editorContent"
                    :disabled="editorLoading || editorSaving" placeholder="（加载中…）"></textarea>
          <div class="flex justify-end gap-2 mt-4">
            <span v-if="editorLoading" class="text-xs text-slate-500 mr-auto">加载中…</span>
            <button class="btn" @click="showEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="editorLoading || editorSaving" @click="saveEditor()">
              {{ editorSaving ? '保存中…' : '保存' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 新建文件夹弹窗 -->
      <div v-if="showMkdir" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">新建文件夹</h3>
          <input class="input font-mono" v-model="mkdirName" placeholder="文件夹名称" @keyup.enter="createMkdir()" />
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showMkdir = false">取消</button>
            <button class="btn btn-primary" @click="createMkdir()">创建</button>
          </div>
        </div>
      </div>

      <!-- 重命名弹窗 -->
      <div v-if="showRename" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">重命名</h3>
          <input class="input font-mono" v-model="renameName" placeholder="新名称" @keyup.enter="saveRename()" />
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showRename = false">取消</button>
            <button class="btn btn-primary" @click="saveRename()">重命名</button>
          </div>
        </div>
      </div>

      <input type="file" ref="uploadInput" class="hidden" @change="onUpload()" />
    </div>
  `,
});
