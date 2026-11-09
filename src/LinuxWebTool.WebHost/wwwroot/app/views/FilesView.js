import { computed, defineComponent, nextTick, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { http, httpUpload, httpDownload } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { buildFileBreadcrumbs } from '../utils/filePaths.js';

// 会话级内存记忆状态（保持 SPA 路由切换时双栈记忆，且遵守 FE-STORAGE 架构门禁）
const targetMemory = {
  container: '/',
  host: '/'
};
let lastActiveTarget = 'container';

export default defineComponent({
  name: 'FilesView',
  setup() {
    const route = useRoute();
    const router = useRouter();
    const currentPath = ref('/');
    const attemptedPath = ref('/');
    const entries = ref([]);
    const breadcrumbs = ref([]);
    const isRoot = ref(true);
    const loading = ref(false);
    const loadError = ref('');
    const directoryReady = ref(false);
    const virtualRoot = ref(false);

    // 目标环境源调度状态机
    const isPrivilegedContainer = ref(false);
    const activeTarget = ref(lastActiveTarget || 'container'); // 'container' | 'host'
    const focusedItem = ref(typeof route.query.focus === 'string' ? route.query.focus : '');

    function applyFocus(name) {
      if (!name) return;
      focusedItem.value = name;
      nextTick(() => {
        const row = document.querySelector(`[data-entry-name="${CSS.escape(name)}"]`);
        if (row) {
          row.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      });
    }

    // 下载状态
    const downloadingPath = ref('');
    const isDownloadingCurrent = computed(() => downloadingPath.value === currentPath.value);

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
    const dirName = computed(() => breadcrumbs.value.at(-1)?.name || '/');
    const containerDisplayName = computed(() => {
      if (!activeTarget.value?.startsWith('docker:')) return '';
      return (route.query.name || activeTarget.value.slice(7, 19));
    });

    async function load(path) {
      const target = path ?? (loadError.value ? attemptedPath.value : currentPath.value);
      attemptedPath.value = target;
      loading.value = true;
      loadError.value = '';
      try {
        let result = await http(API.files.list, {
          method: 'GET',
          params: { path: target, target: activeTarget.value }
        });

        // 核心防御：若传入的是文件路径（后端 404 目录不存在），且路径非根目录，自动回退到父目录并将该文件设为 focus 目标
        if (!result.ok && result.status === 404 && target && target !== '/' && target.includes('/')) {
          const lastSlash = target.lastIndexOf('/');
          const parentDir = lastSlash <= 0 ? '/' : target.slice(0, lastSlash);
          const fileName = target.slice(lastSlash + 1);
          if (fileName) {
            const fallbackResult = await http(API.files.list, {
              method: 'GET',
              params: { path: parentDir, target: activeTarget.value }
            });
            if (fallbackResult.ok && fallbackResult.data) {
              result = fallbackResult;
              applyFocus(fileName);
            }
          }
        }

        if (result.ok && result.data && Array.isArray(result.data.entries)) {
          currentPath.value = result.data.path;
          entries.value = result.data.entries;
          breadcrumbs.value = buildFileBreadcrumbs(result.data.path);
          isRoot.value = result.data.isRoot === true;
          virtualRoot.value = result.data.isVirtualRoot === true;
          if (virtualRoot.value) breadcrumbs.value[0].name = '磁盘';
          directoryReady.value = true;
          targetMemory[activeTarget.value] = currentPath.value;
          if (route.query.focus || focusedItem.value) {
            applyFocus(String(route.query.focus || focusedItem.value));
          }
          return true;
        }
        loadError.value = result.message || '目录加载失败';
        entries.value = [];
        directoryReady.value = false;
      } catch (e) {
        loadError.value = e?.message || '目录加载失败';
        entries.value = [];
        directoryReady.value = false;
      } finally {
        loading.value = false;
      }
    }

    async function goPath(path) {
      if (await load(path)) {
        router.replace({ path: '/files', query: { path: currentPath.value, target: activeTarget.value } });
      }
    }

    async function switchTarget(newTarget) {
      if (activeTarget.value === newTarget) return;
      if (newTarget === 'host' && !isPrivilegedContainer.value) return;

      // 记忆离开前的路径
      targetMemory[activeTarget.value] = currentPath.value;
      activeTarget.value = newTarget;
      lastActiveTarget = newTarget;

      // 恢复进入新源的目标路径
      const restoredPath = targetMemory[newTarget] || '/';
      await goPath(restoredPath);
    }

    function openTerminal(path = currentPath.value) {
      if (!directoryReady.value || virtualRoot.value && path === currentPath.value || loading.value || loadError.value) return;
      const query = {
        cwd: path,
        open: Date.now().toString(36) + Math.random().toString(36).slice(2, 8)
      };
      if (activeTarget.value === 'host') {
        query.target = 'host';
      } else if (activeTarget.value?.startsWith('docker:')) {
        query.target = activeTarget.value;
        if (route.query.name) query.name = route.query.name;
      }
      router.push({ path: '/terminal', query });
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
      if (!t || t.startsWith('0001-')) return '—';
      const d = new Date(t);
      const pad = (x) => String(x).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    function iconFor(entry) {
      if (entry.isDirectory) return '📁';
      const ext = (entry.extension || '').toLowerCase();
      if (['.txt', '.md', '.log', '.json', '.yaml', '.yml', '.cs', '.js', '.ts', '.py', '.sh', '.rs', '.go', '.xml', '.html', '.css', '.ini', '.conf'].includes(ext)) return '📄';
      if (['.mp4', '.mkv', '.mov', '.avi', '.mp3', '.flac', '.wav', '.jpg', '.png', '.gif', '.webp'].includes(ext)) return '🎬';
      return '📄';
    }

    // 打开目录 / 文本文件（非文本文件直接触发下载）
    function openEntry(entry) {
      if (entry.isDirectory) {
        goPath(entry.path);
      } else if (entry.isTextual) {
        openEditor(entry.path);
      } else {
        downloadEntry(entry);
      }
    }

    // 文本查看与编辑
    async function openEditor(path) {
      editorPath.value = path;
      editorName.value = path.split('/').filter(Boolean).pop() || path;
      editorContent.value = '';
      editorLoading.value = true;
      showEditor.value = true;
      try {
        const result = await http(API.files.content, {
          method: 'GET',
          params: { path, target: activeTarget.value }
        });
        if (result.ok && result.data) {
          editorContent.value = result.data.content ?? '';
        } else {
          toast.error(result.message || '读取文件失败');
          showEditor.value = false;
        }
      } catch (e) {
        toast.error(e?.message || '读取文件失败');
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
          body: {
            path: editorPath.value,
            content: editorContent.value,
            target: activeTarget.value
          },
        });
        if (result.ok) {
          toast.success('已保存');
          showEditor.value = false;
          await load();
        } else {
          toast.error(result.message || '保存失败');
        }
      } catch (e) {
        toast.error(e?.message || '保存失败');
      } finally {
        editorSaving.value = false;
      }
    }

    // 下载单文件 / 目录打包
    async function downloadEntry(entry) {
      const p = entry.path;
      downloadingPath.value = p;
      try {
        await httpDownload(API.files.download, {
          params: { path: p, target: activeTarget.value }
        });
      } catch (e) {
        toast.error(e?.message || '下载失败');
      } finally {
        downloadingPath.value = '';
      }
    }

    // 打包下载当前目录
    async function downloadCurrent() {
      if (isRoot.value || !directoryReady.value) return;
      downloadEntry({ path: currentPath.value, isDirectory: true });
    }

    // 在文本查看弹窗中下载当前查看的文件
    async function downloadCurrentEditor() {
      if (!editorPath.value) return;
      downloadEntry({ path: editorPath.value, isDirectory: false });
    }

    // 新建文件夹
    function openMkdir() {
      mkdirName.value = '';
      showMkdir.value = true;
    }

    async function createMkdir() {
      const name = mkdirName.value.trim();
      if (!name) return;
      const base = currentPath.value === '/' ? '' : currentPath.value;
      const newPath = `${base}/${name}`;
      try {
        const result = await http(API.files.mkdir, {
          method: 'POST',
          body: { path: newPath, target: activeTarget.value },
        });
        if (result.ok) {
          toast.success('已创建');
          showMkdir.value = false;
          await load();
        }
      } finally {
        showMkdir.value = false;
      }
    }

    // 重命名
    function renameEntry(entry) {
      renamePath.value = entry.path;
      renameName.value = entry.name;
      showRename.value = true;
    }

    async function saveRename() {
      const newName = renameName.value.trim();
      if (!newName) return;
      const parent = renamePath.value.slice(0, renamePath.value.lastIndexOf('/')) || '';
      const newPath = parent === '' ? `/${newName}` : `${parent}/${newName}`;
      try {
        const result = await http(API.files.rename, {
          method: 'POST',
          body: { from: renamePath.value, to: newPath, target: activeTarget.value },
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
      const targetLabel = activeTarget.value === 'host' ? ' [宿主文件]' : '';
      openConfirm({
        title: `删除${isDir ? '文件夹' : '文件'}${targetLabel}`,
        message: `确定删除「${entry.name}」？${activeTarget.value === 'host' ? '该操作直接作用于物理宿主机，' : ''}此操作不可撤销。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const parentPath = currentPath.value;
          const result = await http(API.files.remove(), {
            method: 'DELETE',
            params: { path: entry.path, target: activeTarget.value }
          });
          if (result.ok) {
            toast.success('已删除');
            await load(parentPath);
          } else if (result && result.data && result.data.needRecursive) {
            openConfirm({
              title: `递归删除文件夹${targetLabel}`,
              message: `「${entry.name}」非空，继续将删除其中所有内容。继续？`,
              confirmText: '强制删除',
              danger: true,
              onConfirm: async () => {
                const r2 = await http(API.files.remove(), {
                  method: 'DELETE',
                  params: { path: entry.path, recursive: true, target: activeTarget.value }
                });
                if (r2.ok) {
                  toast.success('已删除');
                  await load(parentPath);
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
        const result = await httpUpload(API.files.upload(), {
          params: { path: uploadPath.value, target: activeTarget.value },
          file
        });
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

    async function syncFromRoute() {
      const qTarget = route.query.target;
      const candidateTarget = (typeof qTarget === 'string' && qTarget) ? qTarget : (lastActiveTarget || 'container');
      if (candidateTarget.startsWith('docker:')) {
        activeTarget.value = candidateTarget;
      } else if (candidateTarget === 'host' && isPrivilegedContainer.value) {
        activeTarget.value = 'host';
      } else {
        activeTarget.value = 'container';
      }
      lastActiveTarget = activeTarget.value;

      const qFocus = route.query.focus;
      if (typeof qFocus === 'string') {
        focusedItem.value = qFocus;
      }

      const qPath = route.query.path;
      const start = (typeof qPath === 'string' && qPath)
        ? qPath
        : (activeTarget.value === 'host' ? targetMemory.host : targetMemory.container);

      await load(start || '/');
    }

    onMounted(async () => {
      // 1. 探测环境是否具备宿主特权穿透能力
      try {
        const envRes = await http(API.services.environment, { method: 'GET' }).catch(() => null);
        if (envRes && envRes.ok && envRes.data) {
          isPrivilegedContainer.value = envRes.data.isPrivilegedContainer === true;
        }
      } catch {}

      await syncFromRoute();
    });

    watch(
      () => [route.query.path, route.query.target, route.query.focus],
      async ([newPath, newTarget, newFocus]) => {
        if (!directoryReady.value) return;
        if (typeof newFocus === 'string') {
          focusedItem.value = newFocus;
        }
        if (newTarget && newTarget !== activeTarget.value) {
          await syncFromRoute();
        } else if (newPath && newPath !== currentPath.value) {
          await load(newPath);
        } else if (focusedItem.value) {
          applyFocus(focusedItem.value);
        }
      }
    );

    return {
      currentPath, entries, breadcrumbs, isRoot, loading, loadError, dirName, containerDisplayName, directoryReady, virtualRoot, openTerminal,
      isPrivilegedContainer, activeTarget, switchTarget, focusedItem,
      showEditor, editorPath, editorName, editorContent, editorLoading, editorSaving,
      showMkdir, mkdirName, showRename, renameName, renamePath, uploadInput,
      downloadingPath, isDownloadingCurrent,
      load, goPath, goUp, goHome, formatBytes, formatTime, iconFor, openEntry,
      openEditor, saveEditor, openMkdir, createMkdir, renameEntry, saveRename, removeEntry,
      triggerUpload, onUpload, downloadEntry, downloadCurrent, downloadCurrentEditor,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-center gap-2">
        <h2 class="text-sm text-slate-400">文件管理器</h2>

        <!-- 仅在特权容器环境中显示目标切换开关 -->
        <div v-if="isPrivilegedContainer" class="flex items-center bg-slate-900/80 border border-slate-700/60 rounded p-0.5 shrink-0 text-xs ml-2">
          <button 
            type="button"
            class="px-2 py-1 rounded transition-colors flex items-center gap-1 font-medium"
            :class="activeTarget === 'container' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="switchTarget('container')"
            title="浏览当前容器内部文件"
          >
            <span>📦</span>
            <span>当前容器</span>
          </button>
          <button 
            type="button"
            class="px-2 py-1 rounded transition-colors flex items-center gap-1 font-medium"
            :class="activeTarget === 'host' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40 shadow-sm shadow-amber-500/10' : 'text-slate-400 hover:text-slate-200'"
            @click="switchTarget('host')"
            title="穿透至物理宿主机根文件系统 (nsenter)"
          >
            <span>🖥️</span>
            <span>宿主主机 (nsenter穿透)</span>
          </button>
        </div>

        <div class="ml-auto flex gap-1">
          <button class="btn btn-xs" :disabled="!directoryReady || virtualRoot || loading || !!loadError" @click="openTerminal()">在此打开终端</button>
          <button class="btn btn-xs" :disabled="!directoryReady || virtualRoot || isRoot || loading || !!loadError || isDownloadingCurrent" @click="downloadCurrent()">{{ isDownloadingCurrent ? '打包中…' : '📦 打包当前目录' }}</button>
          <button class="btn btn-xs" :disabled="virtualRoot" @click="openMkdir()">＋ 新建文件夹</button>
          <button class="btn btn-xs" :disabled="virtualRoot" @click="triggerUpload()">⬆ 上传</button>
        </div>
      </div>

      <!-- 宿主模式高危琥珀色安全告警横幅 -->
      <div v-if="activeTarget === 'host'" class="panel !border-amber-500/40 bg-amber-500/10 text-amber-300 text-xs px-3.5 py-1.5 flex items-center gap-2">
        <span class="animate-pulse">⚠️</span>
        <span class="font-medium">🖥️ 宿主主机穿透视图 (nsenter)</span>
        <span class="text-amber-300/70 text-[11px]">— 所有写/删操作直接作用于物理宿主机，系统核心关键目录受策略保护。</span>
      </div>

      <!-- 独立 Docker 容器专属文件系统横幅 -->
      <div v-if="activeTarget.startsWith('docker:')" class="panel !border-cyan-500/40 bg-cyan-500/10 text-cyan-300 text-xs px-3.5 py-1.5 flex items-center justify-between gap-2">
        <div class="flex items-center gap-2">
          <span class="animate-pulse">🐳</span>
          <span class="font-medium">Docker 容器文件系统视图：{{ containerDisplayName }}</span>
          <span class="text-cyan-300/70 text-[11px]">— 实时隔离访问容器根文件系统 (VFS /proc/root)</span>
        </div>
        <button type="button" class="btn btn-xs hover:border-cyan-400" @click="switchTarget('container')">返回主容器</button>
      </div>

      <div class="panel p-3 flex items-center gap-2">
        <button class="btn btn-xs shrink-0" @click="goHome()" :title="activeTarget === 'host' ? '宿主根目录' : '容器根目录'">
          {{ activeTarget === 'host' ? '🖥️' : '🏠' }}
        </button>
        <button class="btn btn-xs shrink-0" @click="goUp()" :disabled="isRoot">⬆ 上一级</button>
        <div class="text-xs text-cyan-300/80 font-mono flex items-center gap-1.5 flex-1 min-w-0 overflow-x-auto no-scrollbar py-0.5">
          <template v-for="(crumb, i) in breadcrumbs" :key="crumb.path">
            <span class="text-slate-600 shrink-0">/</span>
            <button class="hover:text-cyan-300 whitespace-nowrap shrink-0" @click="goPath(crumb.path)">{{ crumb.name }}</button>
          </template>
        </div>
        <button class="btn btn-xs ml-auto shrink-0" @click="load()">刷新</button>
      </div>

      <div v-if="loadError" class="panel !border-rose-500/40 bg-rose-500/5 text-rose-200/90 text-xs px-4 py-3 flex items-center gap-3">
        <span>{{ loadError }}</span>
        <button class="btn btn-xs ml-auto" @click="load()">重试</button>
      </div>

      <div class="panel overflow-x-auto list-viewport">
        <table class="data-table min-w-[48rem]">
          <thead>
            <tr>
              <th class="whitespace-nowrap">名称</th>
              <th class="whitespace-nowrap">大小</th>
              <th class="whitespace-nowrap">修改时间</th>
              <th class="text-right whitespace-nowrap">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="!entries.length && !loading && !loadError"><td colspan="4" class="text-slate-600 py-8 text-center">{{ dirName === '/' ? '根目录为空' : '此目录为空' }}</td></tr>
            <tr v-for="entry in entries" :key="entry.path"
                :data-entry-name="entry.name"
                class="transition cursor-pointer"
                :class="focusedItem === entry.name ? 'bg-cyan-500/15 ring-1 ring-cyan-400 shadow-sm shadow-cyan-500/20' : 'hover:bg-cyan-500/5'"
                @click="openEntry(entry)">
              <td>
                <div class="flex items-center gap-1.5 min-w-0">
                  <span class="shrink-0">{{ iconFor(entry) }}</span>
                  <span class="font-mono text-xs truncate" :class="entry.isDirectory ? 'text-cyan-300/80' : 'text-slate-300'">{{ entry.name }}</span>
                  <span v-if="entry.isSymlink" class="badge !border-slate-600/40 text-slate-400 !text-[0.6rem] !py-0 !px-1 shrink-0" title="符号链接">🔗 链接</span>
                </div>
              </td>
              <td class="text-xs text-slate-500 whitespace-nowrap">{{ entry.isDirectory ? '—' : formatBytes(entry.size) }}</td>
              <td class="text-xs text-slate-500 whitespace-nowrap">{{ formatTime(entry.modified) }}</td>
              <td class="text-right whitespace-nowrap text-xs" @click.stop>
                <div class="inline-flex items-center gap-1.5">
                  <button v-if="entry.isDirectory" class="btn btn-xs" @click="openTerminal(entry.path)">在终端打开</button>
                  <button v-if="!virtualRoot" class="btn btn-xs" :disabled="downloadingPath === entry.path" @click="downloadEntry(entry)">{{ downloadingPath === entry.path ? '下载中…' : (entry.isDirectory ? '打包下载' : '下载') }}</button>
                  <button v-if="!virtualRoot" class="btn btn-xs" @click="renameEntry(entry)">重命名</button>
                  <button v-if="!virtualRoot" class="btn btn-xs btn-danger" @click="removeEntry(entry)">删除</button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 文本编辑弹窗 -->
      <div v-if="showEditor" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3">
            <h3 class="font-display text-base text-neon-soft shrink-0">编辑文本 · {{ editorName }}</h3>
            <span v-if="activeTarget === 'host'" class="badge border-amber-500/50 text-amber-300 !text-[0.65rem]">🖥️ 宿主文件</span>
            <span class="text-xs text-slate-500 truncate min-w-0 flex-1 ml-auto text-right" :title="editorPath">{{ editorPath }}</span>
            <button class="btn btn-xs shrink-0" :disabled="downloadingPath === editorPath" @click="downloadCurrentEditor()">⬇ 下载</button>
            <button class="btn btn-xs shrink-0" @click="showEditor = false">✕</button>
          </div>
          <textarea class="input font-mono !text-[0.8rem] flex-1 min-h-[24rem] resize-y" v-model="editorContent"
                    :disabled="editorLoading || editorSaving" placeholder="（加载中…）"></textarea>
          <div class="flex items-center justify-end gap-2 mt-4">
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
        <div class="panel w-full max-w-sm p-5 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-4">
            <h3 class="font-display text-base text-neon-soft">新建文件夹</h3>
            <span v-if="activeTarget === 'host'" class="badge border-amber-500/50 text-amber-300 !text-[0.65rem] ml-auto">🖥️ 宿主</span>
          </div>
          <input class="input font-mono" v-model="mkdirName" placeholder="文件夹名称" @keyup.enter="createMkdir()" />
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showMkdir = false">取消</button>
            <button class="btn btn-primary" @click="createMkdir()">创建</button>
          </div>
        </div>
      </div>

      <!-- 重命名弹窗 -->
      <div v-if="showRename" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-4">
            <h3 class="font-display text-base text-neon-soft">重命名</h3>
            <span v-if="activeTarget === 'host'" class="badge border-amber-500/50 text-amber-300 !text-[0.65rem] ml-auto">🖥️ 宿主</span>
          </div>
          <input class="input font-mono" v-model="renameName" placeholder="新名称" @keyup.enter="saveRename()" />
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showRename = false">取消</button>
            <button class="btn btn-primary" @click="saveRename()">重命名</button>
          </div>
        </div>
      </div>

      <input type="file" ref="uploadInput" class="hidden" @change="onUpload($event)" />
    </div>
  `,
});
