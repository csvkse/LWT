import { computed, defineComponent, onMounted, onUnmounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

export default defineComponent({
  name: 'WorkersView',
  setup() {
    const router = useRouter();
    const workers = ref([]);
    const loading = ref(false);
    const filterMode = ref('all'); // all | service | daemon
    const filterState = ref('all'); // all | running | stopped | degraded
    const searchKeyword = ref('');

    // 新建向导弹窗
    const isCreateModalOpen = ref(false);
    const createForm = ref({
      id: '',
      name: '',
      description: '',
      mode: 0, // 0: Service, 1: Daemon
      runtime: 1, // 0: Node, 1: Python, 2: DotNet, 3: Bash, 4: PowerShell, 5: Batch
      entrypoint: 'worker.py',
      scriptContent: '',
      autoRestart: true,
      enableSqlite: true,
      enableKv: true,
    });

    // 日志抽屉
    const isLogsDrawerOpen = ref(false);
    const currentWorkerForLogs = ref(null);
    const logLines = ref([]);
    const autoScrollLogs = ref(true);
    let logsTimer = null;

    // KV 存储抽屉
    const isKvDrawerOpen = ref(false);
    const currentWorkerForKv = ref(null);
    const kvItems = ref([]);
    const kvKeyInput = ref('');
    const kvValInput = ref('');

    // Git 仓库扫描弹窗
    const isRepoModalOpen = ref(false);
    const repoUrlInput = ref('');
    const isRepoScanning = ref(false);
    const repoScanResult = ref(null);
    const selectedRepoModuleIds = ref([]);

    // Zip 文件上传引用
    const zipFileInput = ref(null);

    // 统计指标
    const stats = computed(() => {
      const list = workers.value;
      const total = list.length;
      const running = list.filter((w) => w.state === 2 || w.state === 1).length;
      const services = list.filter((w) => w.mode === 0).length;
      const daemons = list.filter((w) => w.mode === 1).length;
      const degraded = list.filter((w) => w.state === 5).length;
      return { total, running, services, daemons, degraded };
    });

    // 过滤列表
    const filteredWorkers = computed(() => {
      return workers.value.filter((w) => {
        if (filterMode.value === 'service' && w.mode !== 0) return false;
        if (filterMode.value === 'daemon' && w.mode !== 1) return false;
        if (filterState.value === 'running' && w.state !== 2 && w.state !== 1) return false;
        if (filterState.value === 'stopped' && w.state !== 0) return false;
        if (filterState.value === 'degraded' && w.state !== 5) return false;

        if (searchKeyword.value.trim()) {
          const kw = searchKeyword.value.trim().toLowerCase();
          const matchName = (w.name || '').toLowerCase().includes(kw);
          const matchId = (w.id || '').toLowerCase().includes(kw);
          const matchDesc = (w.description || '').toLowerCase().includes(kw);
          if (!matchName && !matchId && !matchDesc) return false;
        }
        return true;
      });
    });

    async function loadWorkers() {
      loading.value = true;
      try {
        const res = await http(API.workers.list, { method: 'GET' });
        if (res.ok) {
          workers.value = res.data || [];
        } else {
          toast.error(res.message || '获取函数服务列表失败');
        }
      } catch (err) {
        toast.error('网络连接失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function startWorker(w) {
      try {
        const res = await http(API.workers.start(w.id), { method: 'POST' });
        if (res.ok) {
          toast.success('已发送启动指令: ' + w.name);
          await loadWorkers();
        } else {
          toast.error(res.message || '启动失败');
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    async function stopWorker(w) {
      try {
        const res = await http(API.workers.stop(w.id), { method: 'POST' });
        if (res.ok) {
          toast.success('已停止服务: ' + w.name);
          await loadWorkers();
        } else {
          toast.error(res.message || '停止失败');
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    async function restartWorker(w) {
      try {
        const res = await http(API.workers.restart(w.id), { method: 'POST' });
        if (res.ok) {
          toast.success('已重启服务: ' + w.name);
          await loadWorkers();
        } else {
          toast.error(res.message || '重启失败');
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    async function deleteWorker(w) {
      if (!window.confirm('确定彻底删除该服务「' + w.name + '」及其所有数据目录？此操作不可逆！')) return;
      try {
        const res = await http(API.workers.delete(w.id), { method: 'DELETE' });
        if (res.ok) {
          toast.success('已成功删除服务: ' + w.name);
          await loadWorkers();
        } else {
          toast.error(res.message || '删除失败');
        }
      } catch (err) {
        toast.error('删除异常: ' + err.message);
      }
    }

    function openLogsDrawer(w) {
      currentWorkerForLogs.value = w;
      isLogsDrawerOpen.value = true;
      fetchLogs();
      if (logsTimer) clearInterval(logsTimer);
      logsTimer = setInterval(fetchLogs, 2500);
    }

    function closeLogsDrawer() {
      isLogsDrawerOpen.value = false;
      currentWorkerForLogs.value = null;
      if (logsTimer) {
        clearInterval(logsTimer);
        logsTimer = null;
      }
    }

    async function fetchLogs() {
      if (!currentWorkerForLogs.value) return;
      try {
        const res = await http(API.workers.logs(currentWorkerForLogs.value.id));
        if (res.ok) {
          logLines.value = res.data || [];
        }
      } catch { }
    }

    async function openKvDrawer(w) {
      currentWorkerForKv.value = w;
      isKvDrawerOpen.value = true;
      await loadKvItems();
    }

    function closeKvDrawer() {
      isKvDrawerOpen.value = false;
      currentWorkerForKv.value = null;
      kvItems.value = [];
      kvKeyInput.value = '';
      kvValInput.value = '';
    }

    async function loadKvItems() {
      if (!currentWorkerForKv.value) return;
      try {
        const res = await http(API.workers.kv(currentWorkerForKv.value.id), { method: 'GET' });
        if (res.ok) {
          kvItems.value = res.data || [];
        }
      } catch { }
    }

    async function saveKvItem() {
      if (!kvKeyInput.value.trim()) {
        toast.error('Key 键名不能为空');
        return;
      }
      try {
        const res = await http(API.workers.kv(currentWorkerForKv.value.id), {
          method: 'POST',
          body: { key: kvKeyInput.value.trim(), value: kvValInput.value },
        });
        if (res.ok) {
          toast.success('已成功保存 KV 键值对');
          kvKeyInput.value = '';
          kvValInput.value = '';
          await loadKvItems();
        } else {
          toast.error(res.message || '保存失败');
        }
      } catch (err) {
        toast.error('保存异常: ' + err.message);
      }
    }

    async function deleteKvItem(key) {
      if (!window.confirm('确定删除该键值「' + key + '」？')) return;
      try {
        const res = await http(API.workers.kvItem(currentWorkerForKv.value.id, key), { method: 'DELETE' });
        if (res.ok) {
          toast.success('已删除键值: ' + key);
          await loadKvItems();
        } else {
          toast.error(res.message || '删除失败');
        }
      } catch (err) {
        toast.error('删除异常: ' + err.message);
      }
    }

    function jumpToFiles(dir) {
      if (dir) {
        router.push({ path: '/files', query: { path: dir } });
      }
    }

    function downloadSqlite(w) {
      window.open(API.workers.sqliteDownload(w.id), '_blank');
    }

    function copyToClipboard(text) {
      if (!text) return;
      navigator.clipboard.writeText(text).then(() => {
        toast.success('已复制到剪贴板: ' + text);
      }).catch(() => {
        toast.info(text);
      });
    }

    function openFullAccessUrl(path) {
      if (!path) return;
      const full = window.location.origin + path;
      window.open(full, '_blank');
    }

    function formatBytes(bytes) {
      if (!bytes || bytes <= 0) return '0 B';
      if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
      if (bytes >= 1024) return (bytes / 1024).toFixed(1) + ' KB';
      return bytes + ' B';
    }

    function formatUptime(seconds) {
      if (!seconds || seconds <= 0) return '0秒';
      if (seconds < 60) return seconds + '秒';
      const m = Math.floor(seconds / 60);
      const s = seconds % 60;
      if (m < 60) return m + '分' + s + '秒';
      const h = Math.floor(m / 60);
      return h + '小时' + (m % 60) + '分';
    }

    function onRuntimeTemplateChange(runtimeVal) {
      const r = Number(runtimeVal);
      createForm.value.runtime = r;
      if (r === 0) {
        createForm.value.entrypoint = 'worker.js';
        createForm.value.scriptContent = "const http = require('http');\nconst port = process.env.PORT || 8080;\nhttp.createServer((req, res) => {\n  res.writeHead(200, { 'Content-Type': 'application/json' });\n  res.end(JSON.stringify({ status: 'ok', time: new Date() }));\n}).listen(port);";
      } else if (r === 1) {
        createForm.value.entrypoint = 'worker.py';
        createForm.value.scriptContent = "import os, json\nfrom http.server import HTTPServer, BaseHTTPRequestHandler\nPORT = int(os.environ.get('PORT', 8080))\nclass H(BaseHTTPRequestHandler):\n    def do_GET(self):\n        self.send_response(200)\n        self.end_headers()\n        self.wfile.write(b'{\"status\":\"ok\"}')\nHTTPServer(('0.0.0.0', PORT), H).serve_forever()";
      } else if (r === 2) {
        createForm.value.entrypoint = 'Program.cs';
        createForm.value.scriptContent = "using System;\nusing System.Net;\nvar p = Environment.GetEnvironmentVariable(\"PORT\") ?? \"8080\";\nvar l = new HttpListener();\nl.Prefixes.Add($\"http://*:{p}/\");\nl.Start();\nwhile(true) {\n  var c = await l.GetContextAsync();\n  c.Response.Close();\n}";
      } else if (r === 3) {
        createForm.value.mode = 1; // Daemon
        createForm.value.entrypoint = 'worker.sh';
        createForm.value.scriptContent = "#!/usr/bin/env bash\nwhile true; do\n  echo \"[$(date)] Daemon running...\"\n  sleep 30\ndone";
      } else if (r === 4) {
        createForm.value.mode = 1; // Daemon
        createForm.value.entrypoint = 'worker.ps1';
        createForm.value.scriptContent = "while($true) {\n  Write-Output \"[$(Get-Date)] PowerShell Daemon ticking...\"\n  Start-Sleep -Seconds 30\n}";
      }
    }

    async function handleCreateSubmit() {
      if (!createForm.value.id.trim() || !createForm.value.name.trim()) {
        toast.error('Worker ID 和服务名称不能为空');
        return;
      }
      try {
        const res = await http(API.workers.list, {
          method: 'POST',
          body: createForm.value,
        });
        if (res.ok) {
          toast.success('已成功创建/更新服务: ' + createForm.value.name);
          isCreateModalOpen.value = false;
          await loadWorkers();
        } else {
          toast.error(res.message || '创建失败');
        }
      } catch (err) {
        toast.error('提交失败: ' + err.message);
      }
    }

    // Zip 上传
    function triggerZipUpload() {
      if (zipFileInput.value) zipFileInput.value.click();
    }

    async function onZipFileSelected(e) {
      const file = e.target.files?.[0];
      if (!file) return;
      const formData = new FormData();
      formData.append('file', file);
      try {
        toast.info('正在解析并解压上传的 Worker 压缩包...');
        const res = await http(API.workers.importZip, {
          method: 'POST',
          body: formData,
        });
        if (res.ok) {
          toast.success('已成功导入服务: ' + (res.data?.name || 'Worker'));
          await loadWorkers();
        } else {
          toast.error(res.message || '导入失败');
        }
      } catch (err) {
        toast.error('上传异常: ' + err.message);
      } finally {
        if (e.target) e.target.value = '';
      }
    }

    // Git 仓库扫描与批量导入
    async function scanGitRepo() {
      if (!repoUrlInput.value.trim()) {
        toast.error('请输入有效的 Git 仓库地址');
        return;
      }
      isRepoScanning.value = true;
      repoScanResult.value = null;
      selectedRepoModuleIds.value = [];
      try {
        const res = await http(API.workers.importRepoScan, {
          method: 'POST',
          body: { repoUrl: repoUrlInput.value.trim() },
        });
        if (res.ok) {
          repoScanResult.value = res.data;
          selectedRepoModuleIds.value = (res.data?.modules || []).map((m) => m.id);
          toast.success('扫描完成，检测到 ' + (res.data?.modules?.length || 0) + ' 个服务模块！');
        } else {
          toast.error(res.message || '扫描失败，请检查网络或仓库地址有效性');
        }
      } catch (err) {
        toast.error('扫描请求异常: ' + err.message);
      } finally {
        isRepoScanning.value = false;
      }
    }

    async function applyRepoImport() {
      if (!repoScanResult.value || selectedRepoModuleIds.value.length === 0) {
        toast.error('请勾选至少一个待导入的服务模块');
        return;
      }
      try {
        const res = await http(API.workers.importRepoApply, {
          method: 'POST',
          body: {
            tempDirectory: repoScanResult.value.tempDirectory,
            moduleIds: selectedRepoModuleIds.value,
            overwriteExisting: true,
          },
        });
        if (res.ok) {
          toast.success('成功批量导入 ' + (res.data?.importedCount || 0) + ' 个服务模块！');
          isRepoModalOpen.value = false;
          repoScanResult.value = null;
          await loadWorkers();
        } else {
          toast.error(res.message || '导入应用失败');
        }
      } catch (err) {
        toast.error('应用异常: ' + err.message);
      }
    }

    onMounted(() => {
      loadWorkers();
    });

    onUnmounted(() => {
      if (logsTimer) clearInterval(logsTimer);
    });

    return {
      workers,
      loading,
      filterMode,
      filterState,
      searchKeyword,
      stats,
      filteredWorkers,
      isCreateModalOpen,
      createForm,
      isLogsDrawerOpen,
      currentWorkerForLogs,
      logLines,
      autoScrollLogs,
      isKvDrawerOpen,
      currentWorkerForKv,
      kvItems,
      kvKeyInput,
      kvValInput,
      isRepoModalOpen,
      repoUrlInput,
      isRepoScanning,
      repoScanResult,
      selectedRepoModuleIds,
      zipFileInput,
      loadWorkers,
      startWorker,
      stopWorker,
      restartWorker,
      deleteWorker,
      openLogsDrawer,
      closeLogsDrawer,
      openKvDrawer,
      closeKvDrawer,
      saveKvItem,
      deleteKvItem,
      jumpToFiles,
      downloadSqlite,
      copyToClipboard,
      openFullAccessUrl,
      formatBytes,
      formatUptime,
      onRuntimeTemplateChange,
      handleCreateSubmit,
      triggerZipUpload,
      onZipFileSelected,
      scanGitRepo,
      applyRepoImport,
    };
  },
  template: `
    <div class="space-y-4">
      <!-- 隐藏的文件选择器 -->
      <input
        ref="zipFileInput"
        type="file"
        accept=".zip"
        class="hidden"
        @change="onZipFileSelected($event)"
      />

      <!-- 顶栏：标题、简介与全局操作按钮 -->
      <div class="flex flex-col md:flex-row md:items-center justify-between gap-3 border-b border-cyber-line/30 pb-3">
        <div>
          <div class="flex items-center gap-2">
            <span class="text-xl">⚡</span>
            <h1 class="text-lg font-bold text-slate-100">函数服务 (Workers & Daemons)</h1>
            <span class="badge border-cyan-500/30 text-cyan-300 text-xs">SaaS 边缘计算与长时守护</span>
          </div>
          <p class="text-xs text-slate-400 mt-1">
            类 Cloudflare Workers 微服务与长时守护托管平台，支持独立数据源（SQLite/KV）、动态反向代理与防雪崩自愈。
          </p>
        </div>

        <div class="flex flex-wrap items-center gap-2 shrink-0">
          <button
            type="button"
            class="btn btn-sm btn-primary flex items-center gap-1.5 shadow-sm"
            @click="isCreateModalOpen = true"
          >
            <span>➕</span>
            <span>新建服务</span>
          </button>
          <button
            type="button"
            class="btn btn-sm border-slate-700 hover:border-slate-600 bg-slate-900/60 text-slate-200 flex items-center gap-1.5"
            @click="triggerZipUpload()"
          >
            <span>📦</span>
            <span>导入 Zip</span>
          </button>
          <button
            type="button"
            class="btn btn-sm border-slate-700 hover:border-slate-600 bg-slate-900/60 text-slate-200 flex items-center gap-1.5"
            @click="isRepoModalOpen = true"
          >
            <span>🐙</span>
            <span>Git 仓库导入</span>
          </button>
          <button
            type="button"
            class="btn btn-sm border-slate-700 hover:border-slate-600 bg-slate-900/60 text-slate-300"
            :disabled="loading"
            @click="loadWorkers()"
          >
            <span :class="loading ? 'animate-spin inline-block' : ''">🔄</span>
          </button>
        </div>
      </div>

      <!-- 统计指标与卡片看板 -->
      <div class="grid grid-cols-2 sm:grid-cols-5 gap-2.5">
        <div class="panel p-3 border-cyan-500/20 flex flex-col">
          <span class="text-[11px] text-slate-400">总托管服务</span>
          <span class="text-xl font-bold font-mono text-cyan-300 mt-1">{{ stats.total }}</span>
        </div>
        <div class="panel p-3 border-emerald-500/20 flex flex-col">
          <span class="text-[11px] text-slate-400">正在运行 (Running)</span>
          <span class="text-xl font-bold font-mono text-emerald-300 mt-1">{{ stats.running }}</span>
        </div>
        <div class="panel p-3 border-sky-500/20 flex flex-col">
          <span class="text-[11px] text-slate-400">微服务 API</span>
          <span class="text-xl font-bold font-mono text-sky-300 mt-1">{{ stats.services }}</span>
        </div>
        <div class="panel p-3 border-purple-500/20 flex flex-col">
          <span class="text-[11px] text-slate-400">后台长时守护 (Daemon)</span>
          <span class="text-xl font-bold font-mono text-purple-300 mt-1">{{ stats.daemons }}</span>
        </div>
        <div class="panel p-3 border-rose-500/20 flex flex-col col-span-2 sm:col-span-1">
          <span class="text-[11px] text-slate-400">熔断告警 (Degraded)</span>
          <span class="text-xl font-bold font-mono text-rose-400 mt-1">{{ stats.degraded }}</span>
        </div>
      </div>

      <!-- 筛选与搜索控制栏 -->
      <div class="flex flex-wrap items-center justify-between gap-2.5 bg-black/20 p-2.5 rounded-lg border border-cyber-line/30">
        <div class="flex flex-wrap items-center gap-2">
          <!-- 形态筛选 -->
          <div class="flex items-center rounded bg-slate-900/80 p-0.5 border border-slate-700/60 text-xs">
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="filterMode === 'all' ? 'bg-cyan-500/20 text-cyan-300 font-medium' : 'text-slate-400 hover:text-slate-200'"
              @click="filterMode = 'all'"
            >
              全部
            </button>
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="filterMode === 'service' ? 'bg-cyan-500/20 text-cyan-300 font-medium' : 'text-slate-400 hover:text-slate-200'"
              @click="filterMode = 'service'"
            >
              微服务 API
            </button>
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="filterMode === 'daemon' ? 'bg-cyan-500/20 text-cyan-300 font-medium' : 'text-slate-400 hover:text-slate-200'"
              @click="filterMode = 'daemon'"
            >
              长时守护
            </button>
          </div>

          <!-- 状态筛选 -->
          <div class="flex items-center rounded bg-slate-900/80 p-0.5 border border-slate-700/60 text-xs">
            <button
              type="button"
              class="px-2 py-1 rounded transition-colors"
              :class="filterState === 'all' ? 'bg-slate-700 text-slate-200' : 'text-slate-400 hover:text-slate-200'"
              @click="filterState = 'all'"
            >
              全状态
            </button>
            <button
              type="button"
              class="px-2 py-1 rounded transition-colors"
              :class="filterState === 'running' ? 'bg-emerald-500/20 text-emerald-300' : 'text-slate-400 hover:text-slate-200'"
              @click="filterState = 'running'"
            >
              运行中
            </button>
            <button
              type="button"
              class="px-2 py-1 rounded transition-colors"
              :class="filterState === 'stopped' ? 'bg-slate-700/50 text-slate-300' : 'text-slate-400 hover:text-slate-200'"
              @click="filterState = 'stopped'"
            >
              已停止
            </button>
            <button
              type="button"
              class="px-2 py-1 rounded transition-colors"
              :class="filterState === 'degraded' ? 'bg-rose-500/20 text-rose-300' : 'text-slate-400 hover:text-slate-200'"
              @click="filterState = 'degraded'"
            >
              熔断中
            </button>
          </div>
        </div>

        <!-- 搜索输入框 -->
        <div class="relative w-full sm:w-64">
          <input
            v-model="searchKeyword"
            type="text"
            placeholder="搜索服务名称 / ID / 描述..."
            class="input input-sm w-full bg-slate-900/90 border-slate-700/70 text-xs pl-7"
          />
          <span class="absolute left-2.5 top-2 text-slate-500 text-xs">🔍</span>
        </div>
      </div>

      <!-- 服务卡片列表流 -->
      <div v-if="filteredWorkers.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        <span class="text-2xl block mb-2">🍃</span>
        <span>未检索到匹配的函数微服务或守护进程。</span>
      </div>

      <div v-else class="grid grid-cols-1 md:grid-cols-2 gap-3.5">
        <div
          v-for="w in filteredWorkers"
          :key="w.id"
          class="panel p-4 flex flex-col justify-between transition-all border"
          :class="w.state === 2
            ? 'border-cyan-500/40 shadow-[0_0_12px_rgba(34,211,238,0.08)]'
            : (w.state === 5 ? 'border-rose-500/50 bg-rose-950/10' : 'border-cyber-line/40')"
        >
          <!-- 头部：名称、运行时图标、形态与状态徽标 -->
          <div>
            <div class="flex items-start justify-between gap-2 mb-2">
              <div class="flex items-center gap-2 min-w-0 flex-1">
                <span class="text-base shrink-0">
                  {{ w.runtime === 0 ? '🟩' : (w.runtime === 1 ? '🐍' : (w.runtime === 2 ? '🟣' : (w.runtime === 3 ? '🐚' : '🟦'))) }}
                </span>
                <div class="min-w-0">
                  <div class="flex items-center gap-2">
                    <span class="font-bold text-slate-100 text-sm truncate" :title="w.name">{{ w.name }}</span>
                    <span class="text-[10px] font-mono text-slate-500 truncate">#{{ w.id }}</span>
                  </div>
                  <div class="text-[11px] text-slate-400 truncate mt-0.5" :title="w.description || '无详细描述'">
                    {{ w.description || '无详细描述' }}
                  </div>
                </div>
              </div>

              <!-- 状态 Badge -->
              <div class="shrink-0 flex items-center gap-1.5">
                <span
                  v-if="w.state === 2"
                  class="badge border-emerald-500/50 bg-emerald-500/10 text-emerald-300 text-xs flex items-center gap-1"
                >
                  <span class="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse"></span>
                  <span>运行中</span>
                </span>
                <span
                  v-else-if="w.state === 1"
                  class="badge border-cyan-500/50 bg-cyan-500/10 text-cyan-300 text-xs flex items-center gap-1"
                >
                  <span class="w-1.5 h-1.5 rounded-full bg-cyan-400 animate-spin"></span>
                  <span>启动中</span>
                </span>
                <span
                  v-else-if="w.state === 4"
                  class="badge border-amber-500/50 bg-amber-500/10 text-amber-300 text-xs"
                >
                  ⏳ 退避重试中
                </span>
                <span
                  v-else-if="w.state === 5"
                  class="badge border-rose-500/50 bg-rose-500/10 text-rose-300 text-xs flex items-center gap-1"
                  :title="w.lastError"
                >
                  <span>⚠ CrashLoop 熔断</span>
                </span>
                <span v-else class="badge border-slate-700 bg-slate-900/60 text-slate-400 text-xs">
                  已停止
                </span>
              </div>
            </div>

            <!-- 网络路由入口或常驻形态信息 -->
            <div class="bg-black/30 rounded p-2 text-xs font-mono space-y-1 my-2.5 border border-cyber-line/30">
              <div v-if="w.mode === 0" class="flex items-center justify-between gap-1 text-cyan-300">
                <div class="flex items-center gap-1.5 truncate">
                  <span class="text-slate-500 text-[11px] shrink-0">网关路径:</span>
                  <span class="truncate">{{ w.accessUrl || ('/w/' + w.id + '/') }}</span>
                </div>
                <div class="flex items-center gap-1 shrink-0">
                  <button
                    type="button"
                    class="text-[11px] px-1.5 py-0.5 rounded border border-cyan-500/30 text-cyan-400 hover:bg-cyan-500/20 transition-colors"
                    title="复制完整访问 URL"
                    @click="copyToClipboard(w.accessUrl ? (window.location.origin + w.accessUrl) : '')"
                  >
                    📋 复制
                  </button>
                  <button
                    type="button"
                    class="text-[11px] px-1.5 py-0.5 rounded border border-cyan-500/30 text-cyan-400 hover:bg-cyan-500/20 transition-colors"
                    title="新标签页打开访问"
                    @click="openFullAccessUrl(w.accessUrl)"
                  >
                    🔗 打开
                  </button>
                </div>
              </div>
              <div v-else class="text-purple-300 flex items-center gap-1.5">
                <span class="text-slate-500 text-[11px]">形态:</span>
                <span>⚡ 后台长时守护模式 (无需监听 HTTP 端口)</span>
              </div>

              <!-- 进程状态行 -->
              <div class="flex flex-wrap items-center justify-between text-[11px] text-slate-400 pt-1 border-t border-cyber-line/20">
                <div class="flex items-center gap-2">
                  <span v-if="w.processId">PID: <strong class="text-slate-300">{{ w.processId }}</strong></span>
                  <span v-if="w.boundPort">内部端口: <strong class="text-slate-300">:{{ w.boundPort }}</strong></span>
                  <span v-if="w.uptimeSeconds > 0">运行时长: <strong class="text-slate-300">{{ formatUptime(w.uptimeSeconds) }}</strong></span>
                </div>
                <div v-if="w.fastCrashCount > 0" class="text-amber-400 font-medium">
                  瞬崩计数: {{ w.fastCrashCount }}
                </div>
              </div>
            </div>

            <!-- 数据源与沙盒概览：独立 SQLite & 独立 KV -->
            <div class="flex flex-wrap items-center gap-2 text-[11px] text-slate-400 mb-2">
              <!-- SQLite -->
              <div
                v-if="w.enableSqlite"
                class="flex items-center gap-1 px-2 py-0.5 rounded bg-slate-900/60 border border-slate-700/60"
              >
                <span>💾 SQLite:</span>
                <span class="font-mono text-cyan-300">{{ formatBytes(w.sqliteSizeBytes) }}</span>
                <button
                  type="button"
                  class="text-cyan-400 hover:underline ml-1"
                  title="下载 SQLite 数据库文件"
                  @click="downloadSqlite(w)"
                >
                  下载
                </button>
              </div>

              <!-- KV 存储 -->
              <div
                v-if="w.enableKv"
                class="flex items-center gap-1 px-2 py-0.5 rounded bg-slate-900/60 border border-slate-700/60"
              >
                <span>🔑 独立 KV</span>
                <button
                  type="button"
                  class="text-cyan-400 hover:underline ml-1"
                  @click="openKvDrawer(w)"
                >
                  浏览
                </button>
              </div>

              <!-- 沙盒目录直达 -->
              <button
                type="button"
                class="text-[11px] text-slate-400 hover:text-cyan-300 ml-auto flex items-center gap-1"
                title="在文件管理器中直达该服务数据目录"
                @click="jumpToFiles(w.dataDirectory)"
              >
                <span>📂 数据目录直达</span>
              </button>
            </div>
          </div>

          <!-- 底部操作按钮栏 -->
          <div class="flex items-center justify-between gap-2 pt-2 border-t border-cyber-line/30 mt-1">
            <div class="flex items-center gap-1.5">
              <button
                v-if="w.state === 2 || w.state === 1"
                type="button"
                class="btn btn-xs border-amber-600/40 text-amber-300 hover:bg-amber-500/10"
                @click="stopWorker(w)"
              >
                ⏹ 停止
              </button>
              <button
                v-else
                type="button"
                class="btn btn-xs btn-primary"
                @click="startWorker(w)"
              >
                ▶ 启动
              </button>

              <button
                type="button"
                class="btn btn-xs border-slate-700 hover:border-slate-600 text-slate-300"
                @click="restartWorker(w)"
              >
                🔄 重启
              </button>
            </div>

            <div class="flex items-center gap-1.5">
              <button
                type="button"
                class="btn btn-xs border-cyan-500/30 text-cyan-300 hover:bg-cyan-500/10"
                @click="openLogsDrawer(w)"
              >
                📋 终端日志
              </button>
              <button
                type="button"
                class="btn btn-xs border-rose-600/40 text-rose-300 hover:bg-rose-500/10"
                @click="deleteWorker(w)"
              >
                🗑️
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- 弹窗 1：新建函数服务向导 -->
      <div
        v-if="isCreateModalOpen"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm"
      >
        <div class="panel w-full max-w-xl max-h-[90vh] overflow-y-auto p-5 border-cyan-500/40 shadow-2xl space-y-4">
          <div class="flex items-center justify-between border-b border-cyber-line/30 pb-2.5">
            <h3 class="font-bold text-slate-100 text-base">➕ 新建函数服务 / 守护进程</h3>
            <button type="button" class="text-slate-400 hover:text-white" @click="isCreateModalOpen = false">✕</button>
          </div>

          <!-- 快速模板选择 -->
          <div>
            <label class="block text-xs font-semibold text-slate-300 mb-1.5">🚀 选择多语言模板</label>
            <select
              class="select select-sm w-full bg-slate-900 border-slate-700 text-xs"
              @change="onRuntimeTemplateChange($event.target.value)"
            >
              <option value="1">🐍 Python 3 数据接口 API (有请求)</option>
              <option value="0">🟩 Node.js Webhook 极简服务 (有请求)</option>
              <option value="2">🟣 .NET Core C# 顶层语句高性能服务 (有请求)</option>
              <option value="3">🐚 Linux Bash 定时系统巡检 (后台长时守护)</option>
              <option value="4">🟦 PowerShell 跨平台心跳调度 (后台长时守护)</option>
            </select>
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">服务唯一标识 (ID)</label>
              <input
                v-model="createForm.id"
                type="text"
                placeholder="如: my-py-worker"
                class="input input-sm w-full bg-slate-900 border-slate-700 font-mono text-xs"
              />
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">显示名称</label>
              <input
                v-model="createForm.name"
                type="text"
                placeholder="如: 订单事件分发器"
                class="input input-sm w-full bg-slate-900 border-slate-700 text-xs"
              />
            </div>
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">服务描述</label>
            <input
              v-model="createForm.description"
              type="text"
              placeholder="简要说明服务的用途与接口说明..."
              class="input input-sm w-full bg-slate-900 border-slate-700 text-xs"
            />
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">服务形态</label>
              <select v-model="createForm.mode" class="select select-sm w-full bg-slate-900 border-slate-700 text-xs">
                <option :value="0">🌐 有请求微服务 (Service)</option>
                <option :value="1">⚡ 无请求后台守护 (Daemon)</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">入口执行文件名</label>
              <input
                v-model="createForm.entrypoint"
                type="text"
                class="input input-sm w-full bg-slate-900 border-slate-700 font-mono text-xs"
              />
            </div>
          </div>

          <!-- 源码编辑 -->
          <div>
            <div class="flex items-center justify-between text-xs text-slate-400 mb-1">
              <span>入口源码编辑</span>
              <span class="text-[11px] text-cyan-400">支持保存后直接启动</span>
            </div>
            <textarea
              v-model="createForm.scriptContent"
              rows="6"
              class="textarea w-full bg-slate-950 font-mono text-xs text-slate-200 border-slate-800 leading-relaxed"
            ></textarea>
          </div>

          <!-- 特性开关 -->
          <div class="flex items-center gap-4 text-xs text-slate-300">
            <label class="flex items-center gap-1.5 cursor-pointer">
              <input v-model="createForm.autoRestart" type="checkbox" class="checkbox checkbox-xs" />
              <span>进程异常崩溃自动自愈</span>
            </label>
            <label class="flex items-center gap-1.5 cursor-pointer">
              <input v-model="createForm.enableSqlite" type="checkbox" class="checkbox checkbox-xs" />
              <span>挂载专属独立 SQLite</span>
            </label>
            <label class="flex items-center gap-1.5 cursor-pointer">
              <input v-model="createForm.enableKv" type="checkbox" class="checkbox checkbox-xs" />
              <span>启用专属 Key-Value</span>
            </label>
          </div>

          <div class="flex items-center justify-end gap-2 pt-3 border-t border-cyber-line/30">
            <button type="button" class="btn btn-sm btn-ghost text-slate-400" @click="isCreateModalOpen = false">取消</button>
            <button type="button" class="btn btn-sm btn-primary" @click="handleCreateSubmit()">立即创建并初始化</button>
          </div>
        </div>
      </div>

      <!-- 弹窗 2：终端实时日志抽屉 -->
      <div
        v-if="isLogsDrawerOpen"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/80 backdrop-blur-sm"
      >
        <div class="panel w-full max-w-3xl h-[80vh] flex flex-col p-4 border-cyan-500/40 shadow-2xl">
          <div class="flex items-center justify-between border-b border-cyber-line/30 pb-2 mb-2">
            <div class="flex items-center gap-2">
              <span class="text-base">📋</span>
              <span class="font-bold text-slate-100 text-sm">实时日志流: {{ currentWorkerForLogs?.name }}</span>
              <span class="badge border-cyan-500/30 text-cyan-300 text-xs">RingBuffer (最新200行)</span>
            </div>
            <div class="flex items-center gap-2">
              <button
                type="button"
                class="btn btn-xs border-slate-700 text-slate-300"
                @click="logLines = []"
              >
                清屏
              </button>
              <button
                type="button"
                class="text-slate-400 hover:text-white px-1.5"
                @click="closeLogsDrawer()"
              >
                ✕
              </button>
            </div>
          </div>

          <div class="flex-1 overflow-y-auto bg-black/90 rounded p-3 font-mono text-xs text-emerald-400 leading-relaxed border border-cyber-line/30 select-text">
            <div v-if="logLines.length === 0" class="text-slate-600 italic">暂无输出日志...</div>
            <div v-for="(line, idx) in logLines" :key="idx" class="whitespace-pre-wrap break-all hover:bg-slate-900/50">
              {{ line }}
            </div>
          </div>
        </div>
      </div>

      <!-- 弹窗 3：独立 Key-Value 数据浏览器 -->
      <div
        v-if="isKvDrawerOpen"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/80 backdrop-blur-sm"
      >
        <div class="panel w-full max-w-2xl max-h-[85vh] flex flex-col p-4 border-cyan-500/40 shadow-2xl">
          <div class="flex items-center justify-between border-b border-cyber-line/30 pb-2 mb-3">
            <div class="flex items-center gap-2">
              <span class="text-base">🔑</span>
              <span class="font-bold text-slate-100 text-sm">专属 Key-Value 数据管理: {{ currentWorkerForKv?.name }}</span>
            </div>
            <button type="button" class="text-slate-400 hover:text-white" @click="closeKvDrawer()">✕</button>
          </div>

          <!-- 新增/修改 KV 表单 -->
          <div class="flex items-center gap-2 mb-3">
            <input
              v-model="kvKeyInput"
              type="text"
              placeholder="Key 键名..."
              class="input input-sm w-1/3 bg-slate-900 border-slate-700 text-xs font-mono"
            />
            <input
              v-model="kvValInput"
              type="text"
              placeholder="Value 键值内容..."
              class="input input-sm flex-1 bg-slate-900 border-slate-700 text-xs font-mono"
            />
            <button type="button" class="btn btn-sm btn-primary shrink-0" @click="saveKvItem()">保存</button>
          </div>

          <div class="flex-1 overflow-y-auto border border-cyber-line/30 rounded bg-black/30">
            <table class="table w-full text-xs">
              <thead class="bg-slate-900/80 text-slate-400">
                <tr>
                  <th class="w-1/3">Key</th>
                  <th>Value</th>
                  <th class="w-20 text-right">操作</th>
                </tr>
              </thead>
              <tbody>
                <tr v-if="kvItems.length === 0">
                  <td colspan="3" class="text-center text-slate-600 py-6 italic">暂无 KV 存储记录</td>
                </tr>
                <tr v-for="item in kvItems" :key="item.key" class="hover:bg-slate-900/50">
                  <td class="font-mono text-cyan-300 font-medium truncate">{{ item.key }}</td>
                  <td class="font-mono text-slate-300 truncate max-w-xs" :title="item.value">{{ item.value }}</td>
                  <td class="text-right">
                    <button
                      type="button"
                      class="text-rose-400 hover:underline text-xs"
                      @click="deleteKvItem(item.key)"
                    >
                      删除
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <!-- 弹窗 4：Git 仓库多服务扫描导入向导 -->
      <div
        v-if="isRepoModalOpen"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/80 backdrop-blur-sm"
      >
        <div class="panel w-full max-w-2xl max-h-[85vh] flex flex-col p-4 border-cyan-500/40 shadow-2xl space-y-3">
          <div class="flex items-center justify-between border-b border-cyber-line/30 pb-2">
            <div class="flex items-center gap-2">
              <span class="text-base">🐙</span>
              <span class="font-bold text-slate-100 text-sm">Git 单仓多服务扫描导入</span>
            </div>
            <button type="button" class="text-slate-400 hover:text-white" @click="isRepoModalOpen = false">✕</button>
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">Git 仓库地址 (支持完整 URL 或 owner/repo 缩写)</label>
            <div class="flex items-center gap-2">
              <input
                v-model="repoUrlInput"
                type="text"
                placeholder="例如: https://github.com/my-org/workers-suite.git"
                class="input input-sm flex-1 bg-slate-900 border-slate-700 text-xs font-mono"
              />
              <button
                type="button"
                class="btn btn-sm btn-primary shrink-0"
                :disabled="isRepoScanning"
                @click="scanGitRepo()"
              >
                <span :class="isRepoScanning ? 'animate-spin inline-block' : ''">🔍</span>
                <span>{{ isRepoScanning ? '正在浅克隆扫描...' : '扫描仓库' }}</span>
              </button>
            </div>
          </div>

          <!-- 扫描结果列表 -->
          <div v-if="repoScanResult" class="flex-1 overflow-y-auto space-y-2 border border-cyber-line/30 rounded p-2 bg-black/20">
            <div class="flex items-center justify-between text-xs text-slate-400 px-1">
              <span>已发现 {{ repoScanResult.modules.length }} 个子服务模块</span>
              <button
                type="button"
                class="text-cyan-400 hover:underline"
                @click="selectedRepoModuleIds = repoScanResult.modules.map(m => m.id)"
              >
                全选
              </button>
            </div>

            <div
              v-for="mod in repoScanResult.modules"
              :key="mod.id"
              class="panel p-2.5 flex items-center justify-between border-slate-800 hover:border-slate-700 bg-slate-900/60"
            >
              <div class="flex items-center gap-2.5 min-w-0">
                <input
                  v-model="selectedRepoModuleIds"
                  :value="mod.id"
                  type="checkbox"
                  class="checkbox checkbox-xs"
                />
                <div class="min-w-0">
                  <div class="flex items-center gap-2">
                    <span class="font-bold text-slate-200 text-xs truncate">{{ mod.name }}</span>
                    <span class="badge border-cyan-500/30 text-cyan-300 text-[10px]">#{{ mod.id }}</span>
                    <span v-if="mod.alreadyInstalled" class="badge border-amber-500/40 text-amber-300 text-[10px]">已存在将覆盖</span>
                  </div>
                  <div class="text-[11px] text-slate-400 truncate mt-0.5">{{ mod.description || mod.relativePath }}</div>
                </div>
              </div>
              <span class="badge border-slate-700 text-slate-400 text-[10px] shrink-0 font-mono">
                {{ mod.mode === 0 ? '微服务 API' : '长时守护' }}
              </span>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 pt-2 border-t border-cyber-line/30">
            <button type="button" class="btn btn-sm btn-ghost text-slate-400" @click="isRepoModalOpen = false">取消</button>
            <button
              type="button"
              class="btn btn-sm btn-primary"
              :disabled="!repoScanResult || selectedRepoModuleIds.length === 0"
              @click="applyRepoImport()"
            >
              批量导入已勾选服务 ({{ selectedRepoModuleIds.length }})
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
