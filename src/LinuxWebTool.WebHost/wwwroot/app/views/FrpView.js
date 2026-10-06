import { computed, defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { formatBytes, formatDuration, formatTime } from '../utils/format.js';

export default defineComponent({
  name: 'FrpView',
  setup() {
    const lines = ref([]);
    const logs = ref([]);
    const loading = ref(false);
    const saving = ref(false);
    const selectedLineId = ref('all');
    let timer = null;

    // 模态框：新建 / 编辑线路
    const showModal = ref(false);
    const isEdit = ref(false);
    const currentId = ref(null);
    const showApiKey = ref(false);

    const form = reactive({
      name: '',
      serverUrl: '',
      backupServerUrls: '',
      tunnelHost: '',
      apiKey: '',
      localTargetUrl: 'http://127.0.0.1:8080',
      autoStart: true,
      heartbeatIntervalSeconds: 15,
      enableLan302Proxy: true,
      proxyType: 'Direct',
      proxyUrl: '',
      proxyBypass: '',
      sortOrder: 0,
    });

    // 统计总指标
    const summary = computed(() => {
      let activeCount = 0;
      let totalSent = 0;
      let totalReceived = 0;

      for (const l of lines.value) {
        if (l.state === 'Connected') activeCount++;
        totalSent += l.sentBytes || 0;
        totalReceived += l.receivedBytes || 0;
      }

      return {
        totalLines: lines.value.length,
        activeCount,
        totalSent,
        totalReceived,
      };
    });

    async function loadLines() {
      const res = await http(API.frp.lines, { method: 'GET' });
      if (res.ok && res.data) {
        lines.value = res.data;
      }
    }

    async function loadLogs() {
      if (selectedLineId.value === 'all') {
        const res = await http(API.frp.logs);
        if (res.ok && res.data) logs.value = res.data;
      } else {
        const res = await http(API.frp.lineLogs(selectedLineId.value));
        if (res.ok && res.data) logs.value = res.data;
      }
    }

    async function refreshAll() {
      loading.value = true;
      await Promise.all([loadLines(), loadLogs()]);
      loading.value = false;
    }

    function openCreate() {
      isEdit.value = false;
      currentId.value = null;
      showApiKey.value = false;
      Object.assign(form, {
        name: '',
        serverUrl: '',
        backupServerUrls: '',
        tunnelHost: 'line_' + Math.random().toString(36).substring(2, 7),
        apiKey: '',
        localTargetUrl: 'http://127.0.0.1:8080',
        autoStart: true,
        heartbeatIntervalSeconds: 15,
        enableLan302Proxy: true,
        proxyType: 'Direct',
        proxyUrl: '',
        proxyBypass: '',
        sortOrder: 0,
      });
      showModal.value = true;
    }

    function openEdit(line) {
      isEdit.value = true;
      currentId.value = line.id;
      showApiKey.value = false;
      Object.assign(form, {
        name: line.name,
        serverUrl: line.serverUrl,
        backupServerUrls: line.backupServerUrls || '',
        tunnelHost: line.tunnelHost,
        apiKey: line.apiKey || '',
        localTargetUrl: line.localTargetUrl,
        autoStart: line.autoStart,
        heartbeatIntervalSeconds: line.heartbeatIntervalSeconds || 15,
        enableLan302Proxy: line.enableLan302Proxy !== false,
        proxyType: line.proxyType || 'Direct',
        proxyUrl: line.proxyUrl || '',
        proxyBypass: line.proxyBypass || '',
        sortOrder: line.sortOrder || 0,
      });
      showModal.value = true;
    }

    async function saveLine() {
      if (!form.name.trim()) return toast.error('请输入线路名称');
      if (!form.serverUrl.trim()) return toast.error('请输入服务端 WebSocket URL');
      if (!form.tunnelHost.trim()) return toast.error('请输入分配的公网 Host');
      if (!form.localTargetUrl.trim()) return toast.error('请输入本地目标地址');

      saving.value = true;
      const payload = {
        name: form.name.trim(),
        serverUrl: form.serverUrl.trim(),
        backupServerUrls: form.backupServerUrls.trim() || null,
        tunnelHost: form.tunnelHost.trim().toLowerCase(),
        apiKey: form.apiKey.trim(),
        localTargetUrl: form.localTargetUrl.trim(),
        autoStart: form.autoStart,
        heartbeatIntervalSeconds: Number(form.heartbeatIntervalSeconds) || 15,
        enableLan302Proxy: form.enableLan302Proxy,
        proxyType: form.proxyType,
        proxyUrl: form.proxyUrl.trim() || null,
        proxyBypass: form.proxyBypass.trim() || null,
        sortOrder: Number(form.sortOrder) || 0,
      };

      const res = isEdit.value
        ? await http(API.frp.lineItem(currentId.value), { method: 'PUT', body: payload })
        : await http(API.frp.lines, { method: 'POST', body: payload });

      saving.value = false;
      if (res.ok) {
        toast.success(isEdit.value ? '穿透线路已更新' : '穿透线路已创建');
        showModal.value = false;
        loadLines();
      }
    }

    async function removeLine(line) {
      if (!confirm(`确定要永久删除穿透线路 "${line.name}" (${line.tunnelHost}) 吗？`)) return;
      const res = await http(API.frp.lineItem(line.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        loadLines();
      }
    }

    async function startLine(line) {
      const res = await http(API.frp.lineStart(line.id), { method: 'POST' });
      if (res.ok) {
        toast.success('已触发启动连接');
        line.state = 'Connecting';
        loadLines();
      }
    }

    async function stopLine(line) {
      const res = await http(API.frp.lineStop(line.id), { method: 'POST' });
      if (res.ok) {
        toast.success('已断开穿透连接');
        line.state = 'Stopped';
        loadLines();
      }
    }

    function viewLineLogs(lineId) {
      selectedLineId.value = lineId;
      loadLogs();
    }

    function stateBadge(state) {
      switch (state) {
        case 'Connected':
          return { label: '● 运行中', class: 'border-emerald-500/60 text-emerald-300 bg-emerald-950/40' };
        case 'Connecting':
          return { label: '◐ 连接中', class: 'border-amber-500/60 text-amber-300 bg-amber-950/40' };
        case 'Reconnecting':
          return { label: '⟳ 重连中', class: 'border-cyan-500/60 text-cyan-300 bg-cyan-950/40' };
        case 'Displaced':
          return { label: '✕ 被顶替', class: 'border-rose-500/60 text-rose-300 bg-rose-950/40' };
        default:
          return { label: '○ 已停止', class: 'border-slate-600 text-slate-400 bg-slate-900/60' };
      }
    }

    onMounted(() => {
      refreshAll();
      timer = setInterval(refreshAll, 6000);
    });

    onUnmounted(() => {
      if (timer) clearInterval(timer);
    });

    return {
      lines,
      logs,
      loading,
      saving,
      showModal,
      isEdit,
      form,
      showApiKey,
      selectedLineId,
      summary,
      loadLines,
      loadLogs,
      refreshAll,
      openCreate,
      openEdit,
      saveLine,
      removeLine,
      startLine,
      stopLine,
      viewLineLogs,
      stateBadge,
      formatBytes,
      formatDuration,
      formatTime,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <!-- 顶部标题与操作栏 -->
      <div class="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold text-slate-100 flex items-center gap-2">
            ProxyByCF FRP 内网穿透客户端
            <span class="badge border-cyan-500/40 text-cyan-400 bg-cyan-950/30">多线路增强版</span>
          </h1>
          <p class="text-xs text-slate-400 mt-1">
            纯 C# 原生 WebSocket 反向穿透隧道，支持多业务线路独立运行、自动代理 302 内网地址 (智能私网代拉 + 公网 CDN 放行) 以及 SOCKS5/HTTP 出网网络代理。
          </p>
        </div>
        <div class="flex items-center gap-2">
          <button class="btn btn-secondary" @click="refreshAll()">
            <span>刷新状态</span>
          </button>
          <button class="btn btn-primary" @click="openCreate()">
            <span>+ 新建穿透线路</span>
          </button>
        </div>
      </div>

      <!-- 顶层汇总状态卡片 -->
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
        <div class="panel p-4 flex flex-col justify-between">
          <span class="text-xs text-slate-400">穿透线路总数</span>
          <div class="mt-2 text-2xl font-mono text-slate-100 flex items-baseline gap-2">
            <span>{{ summary.totalLines }}</span>
            <span class="text-xs font-normal text-slate-400">条配置</span>
          </div>
          <span class="text-[11px] text-slate-500 mt-2 truncate">
            在线运行: <span class="text-emerald-400 font-semibold">{{ summary.activeCount }}</span> 条
          </span>
        </div>

        <div class="panel p-4 flex flex-col justify-between">
          <span class="text-xs text-slate-400">智能 302 内网代理</span>
          <div class="mt-2 text-sm font-medium text-cyan-300 flex items-center gap-1.5">
            <span class="inline-block w-2 h-2 rounded-full bg-cyan-400 animate-pulse"></span>
            自适应代拉已就绪
          </div>
          <span class="text-[11px] text-slate-500 mt-2 truncate">
            自动识别私网 302 拉流 / 公网 CDN 放行
          </span>
        </div>

        <div class="panel p-4 flex flex-col justify-between">
          <span class="text-xs text-slate-400">上行发送流量</span>
          <div class="mt-2 text-xl font-mono text-cyan-300">
            ↑ {{ formatBytes(summary.totalSent) }}
          </div>
          <span class="text-[11px] text-slate-500 mt-2">双向异步切片流</span>
        </div>

        <div class="panel p-4 flex flex-col justify-between">
          <span class="text-xs text-slate-400">下行接收流量</span>
          <div class="mt-2 text-xl font-mono text-violet-300">
            ↓ {{ formatBytes(summary.totalReceived) }}
          </div>
          <span class="text-[11px] text-slate-500 mt-2">包含分片媒体与请求体</span>
        </div>
      </div>

      <!-- 线路列表网格卡片 -->
      <div v-if="lines.length === 0" class="panel p-12 text-center text-slate-400 text-xs">
        <p>暂无配置的穿透线路，请点击右上角「+ 新建穿透线路」添加您的第一个服务映射。</p>
      </div>

      <div v-else class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        <div v-for="line in lines" :key="line.id"
             class="panel p-4.5 flex flex-col justify-between border-slate-700/60 hover:border-slate-600 transition-colors">
          <!-- 线路卡片头部 -->
          <div>
            <div class="flex items-start justify-between gap-2 border-b border-slate-700/50 pb-3">
              <div>
                <h3 class="font-semibold text-slate-100 text-sm flex items-center gap-2">
                  {{ line.name }}
                </h3>
                <span class="font-mono text-xs text-cyan-400 mt-0.5 block truncate">
                  Host: {{ line.tunnelHost }}
                </span>
              </div>
              <span class="badge text-[11px] shrink-0" :class="stateBadge(line.state).class">
                {{ stateBadge(line.state).label }}
              </span>
            </div>

            <!-- 核心参数与特性徽章 -->
            <div class="flex flex-wrap gap-1.5 my-3">
              <span v-if="line.autoStart" class="badge text-[10px] border-emerald-500/40 text-emerald-300 bg-emerald-950/20">
                自启
              </span>
              <span v-if="line.enableLan302Proxy" class="badge text-[10px] border-cyan-500/40 text-cyan-300 bg-cyan-950/20">
                302私网代拉
              </span>
              <span v-if="line.proxyType === 'Socks5'" class="badge text-[10px] border-purple-500/40 text-purple-300 bg-purple-950/20">
                SOCKS5 出网
              </span>
              <span v-else-if="line.proxyType === 'Http'" class="badge text-[10px] border-amber-500/40 text-amber-300 bg-amber-950/20">
                HTTP 出网
              </span>
              <span v-else class="badge text-[10px] border-slate-700 text-slate-400 bg-slate-900">
                直连出网
              </span>
            </div>

            <!-- 地址信息展示 -->
            <div class="flex flex-col gap-1.5 text-xs bg-slate-900/60 p-2.5 rounded border border-slate-800">
              <div>
                <span class="text-slate-500 block text-[11px]">公网入口:</span>
                <a v-if="line.publicUrl" :href="line.publicUrl" target="_blank"
                   class="font-mono text-cyan-300 hover:underline flex items-center gap-1 truncate mt-0.5">
                  {{ line.publicUrl }} ↗
                </a>
                <span v-else class="font-mono text-slate-500 text-[11px]">等待长连接建立</span>
              </div>

              <div>
                <span class="text-slate-500 block text-[11px]">本地目标:</span>
                <span class="font-mono text-slate-300 truncate block mt-0.5">
                  {{ line.localTargetUrl }}
                </span>
              </div>

              <div v-if="line.proxyUrl" class="truncate">
                <span class="text-slate-500 block text-[11px]">前置代理:</span>
                <span class="font-mono text-purple-300 text-[11px] truncate block">
                  {{ line.proxyUrl }}
                </span>
              </div>
            </div>

            <!-- 实时统计数据 -->
            <div class="grid grid-cols-2 gap-2 mt-3 text-[11px] font-mono text-slate-400">
              <div>运行: {{ line.uptimeSeconds > 0 ? formatDuration(line.uptimeSeconds) : '--' }}</div>
              <div class="text-right text-cyan-300">↑ {{ formatBytes(line.sentBytes) }}</div>
              <div>心跳: {{ line.heartbeatIntervalSeconds }}s</div>
              <div class="text-right text-violet-300">↓ {{ formatBytes(line.receivedBytes) }}</div>
            </div>

            <!-- 异常提示条 -->
            <div v-if="line.lastError" class="mt-2.5 p-2 rounded bg-rose-950/40 border border-rose-500/40 text-[11px] text-rose-300 font-mono truncate">
              ⚠️ {{ line.lastError }}
            </div>
          </div>

          <!-- 卡片底部操作按钮 -->
          <div class="flex items-center justify-between border-t border-slate-700/50 pt-3 mt-3">
            <div class="flex items-center gap-1.5">
              <button class="btn btn-xs" @click="viewLineLogs(line.id)">日志</button>
              <button class="btn btn-xs" @click="openEdit(line)">编辑</button>
              <button class="btn btn-xs btn-danger" @click="removeLine(line)">删除</button>
            </div>
            <div>
              <button v-if="line.state !== 'Connected'" class="btn btn-xs btn-primary" @click="startLine(line)">
                ▶ 启动
              </button>
              <button v-else class="btn btn-xs btn-danger" @click="stopLine(line)">
                ■ 断开
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- 实时日志控制台面板 -->
      <div class="panel p-5 flex flex-col gap-3">
        <div class="border-b border-slate-700/60 pb-3 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-2">
          <div class="flex items-center gap-2">
            <h2 class="font-semibold text-slate-100 text-sm flex items-center gap-2">
              穿透引擎实时日志
            </h2>
            <select v-model="selectedLineId" class="input py-0.5 text-xs font-mono" @change="loadLogs()">
              <option value="all">全量所有线路日志</option>
              <option v-for="l in lines" :key="l.id" :value="l.id">{{ l.name }} ({{ l.tunnelHost }})</option>
            </select>
          </div>
          <button class="btn btn-xs" @click="loadLogs()">刷新日志</button>
        </div>

        <div class="bg-slate-950/90 rounded border border-slate-800 p-3 h-[280px] overflow-y-auto font-mono text-[11px] flex flex-col gap-1.5">
          <div v-if="logs.length === 0" class="text-slate-500 text-center py-12">
            暂无日志记录
          </div>
          <div v-for="(log, idx) in logs" :key="idx" class="leading-relaxed flex items-start gap-2">
            <span class="text-slate-500 shrink-0">{{ formatTime(log.timestamp) }}</span>
            <span class="badge text-[10px] py-0 shrink-0"
                  :class="log.level === 'ERR' ? 'border-rose-500/60 text-rose-300' : (log.level === 'WRN' ? 'border-amber-500/60 text-amber-300' : 'border-cyan-500/40 text-cyan-300')">
              {{ log.level }}
            </span>
            <span class="text-slate-300 break-all">{{ log.message }}</span>
          </div>
        </div>
      </div>

      <!-- 新建/编辑线路模态框 -->
      <div v-if="showModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEdit ? '编辑穿透线路' : '新建穿透线路' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <label class="block text-slate-400 mb-1">线路名称 <span class="text-rose-400">*</span></label>
              <input v-model="form.name" class="input" placeholder="例如：群晖 DSM / 运维主控 / Emby 影音" />
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-400 mb-1">分配公网 Host <span class="text-rose-400">*</span></label>
                <input v-model="form.tunnelHost" class="input font-mono" placeholder="nas / lwt / myapp" />
                <span class="text-[11px] text-slate-500 mt-0.5 block">小写字母/数字，唯一标识</span>
              </div>
              <div>
                <label class="block text-slate-400 mb-1">心跳间隔（秒）</label>
                <input v-model.number="form.heartbeatIntervalSeconds" type="number" min="5" max="120" class="input font-mono" />
              </div>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">服务端 WebSocket 地址 <span class="text-rose-400">*</span></label>
              <input v-model="form.serverUrl" class="input font-mono" placeholder="wss://edge.example.com/frp" />
            </div>

            <div>
              <label class="block text-slate-400 mb-1">客户端鉴权 Token (ApiKey)</label>
              <div class="relative">
                <input v-model="form.apiKey" :type="showApiKey ? 'text' : 'password'" class="input font-mono pr-12" placeholder="与边缘网关端保持一致" />
                <button type="button" class="absolute right-2 top-2 text-[11px] text-cyan-400 hover:text-cyan-300" @click="showApiKey = !showApiKey">
                  {{ showApiKey ? '隐藏' : '显示' }}
                </button>
              </div>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">本地转发目标地址 <span class="text-rose-400">*</span></label>
              <input v-model="form.localTargetUrl" class="input font-mono" placeholder="http://127.0.0.1:8080 或 http://192.168.1.200:5000" />
            </div>

            <!-- 高级特性配置：302 代理与自启 -->
            <div class="border-t border-slate-800/80 pt-3 flex flex-col gap-2">
              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="form.enableLan302Proxy" class="accent-cyan-500" />
                <div>
                  <span class="text-slate-200 font-medium block">自动代理 302 重定向内网地址 (智能私网代拉)</span>
                  <span class="text-[11px] text-slate-400 block">拦截本地返回的私网 302（192.168.x / 容器名）并在内网代拉流；若为公网 CDN 直链则放行</span>
                </div>
              </label>

              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="form.autoStart" class="accent-emerald-500" />
                <span class="text-slate-200">随应用开机自启该线路</span>
              </label>
            </div>

            <!-- 网络出网代理设置 (SOCKS5 / HTTP) -->
            <div class="border-t border-slate-800/80 pt-3 flex flex-col gap-2">
              <label class="block font-medium text-slate-300">上游出网代理设置 (Egress Proxy)</label>
              <div class="grid grid-cols-2 sm:grid-cols-4 gap-2">
                <label class="flex items-center gap-1.5 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="radio" v-model="form.proxyType" value="Direct" class="accent-cyan-500" />
                  <span class="text-slate-300">直连出网</span>
                </label>
                <label class="flex items-center gap-1.5 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="radio" v-model="form.proxyType" value="Socks5" class="accent-purple-500" />
                  <span class="text-slate-300">SOCKS5</span>
                </label>
                <label class="flex items-center gap-1.5 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="radio" v-model="form.proxyType" value="Http" class="accent-amber-500" />
                  <span class="text-slate-300">HTTP 代理</span>
                </label>
                <label class="flex items-center gap-1.5 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="radio" v-model="form.proxyType" value="System" class="accent-blue-500" />
                  <span class="text-slate-300">系统环境变量</span>
                </label>
              </div>

              <div v-if="form.proxyType === 'Socks5' || form.proxyType === 'Http'">
                <label class="block text-slate-400 mb-1">代理服务器地址 (包含认证可选)</label>
                <input v-model="form.proxyUrl" class="input font-mono"
                       :placeholder="form.proxyType === 'Socks5' ? 'socks5://127.0.0.1:7890' : 'http://user:pass@192.168.1.1:8080'" />
                <span class="text-[11px] text-slate-500 mt-0.5 block">WebSocket 隧道经此代理中转出网，内网 302 代拉与本地转发强制直连绕过代理</span>
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showModal = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="saveLine()">
              {{ saving ? '保存中...' : '保存线路' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
