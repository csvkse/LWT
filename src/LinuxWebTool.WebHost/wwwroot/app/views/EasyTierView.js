import { computed, defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { http, httpUpload } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { formatBytes } from '../utils/format.js';

export default defineComponent({
  name: 'EasyTierView',
  setup() {
    const nodes = ref([]);
    const loading = ref(false);
    const saving = ref(false);
    const engineStatus = ref(null);
    const activeNodeDetail = ref(null);
    let refreshTimer = null;

    // 模态框：新建 / 编辑节点
    const showNodeModal = ref(false);
    const isEditNode = ref(false);
    const editNodeId = ref(null);
    const activeTab = ref('wizard'); // 'wizard' | 'toml'

    const activeBasePort = ref(11010);
    const activeWgPort = ref(11011);
    const portIncrementInfo = ref('');

    function extractPortsFromString(str) {
      if (!str || typeof str !== 'string') return [];
      const found = [];
      const regex = /:(\d{2,5})\b/g;
      let match;
      while ((match = regex.exec(str)) !== null) {
        const p = parseInt(match[1], 10);
        if (p >= 1 && p <= 65535) {
          found.push(p);
        }
      }
      for (const line of str.split('\n')) {
        const trimmed = line.trim();
        if (/^\d{2,5}$/.test(trimmed)) {
          const p = parseInt(trimmed, 10);
          if (p >= 1 && p <= 65535) {
            found.push(p);
          }
        }
      }
      return found;
    }

    function buildListenersText(basePort, wgPort, type = 'all_dual_stack') {
      if (type === 'all_dual_stack') {
        return [
          `tcp://0.0.0.0:${basePort}`,
          `udp://0.0.0.0:${basePort}`,
          `wg://0.0.0.0:${wgPort}`,
          `tcp://[::]:${basePort}`,
          `udp://[::]:${basePort}`,
          `wg://[::]:${wgPort}`,
        ].join('\n');
      }
      if (type === 'all_ipv4') {
        return [
          `tcp://0.0.0.0:${basePort}`,
          `udp://0.0.0.0:${basePort}`,
          `wg://0.0.0.0:${wgPort}`,
        ].join('\n');
      }
      if (type === 'udp_dual_stack') {
        return [
          `udp://0.0.0.0:${basePort}`,
          `udp://[::]:${basePort}`,
        ].join('\n');
      }
      if (type === 'tcp_udp_dual') {
        return [
          `tcp://0.0.0.0:${basePort}`,
          `udp://0.0.0.0:${basePort}`,
          `tcp://[::]:${basePort}`,
          `udp://[::]:${basePort}`,
        ].join('\n');
      }
      return '';
    }

    function computeNextNodePorts() {
      let maxPort = 0;
      for (const node of nodes.value || []) {
        if (Array.isArray(node.listeners)) {
          for (const lis of node.listeners) {
            const ports = extractPortsFromString(lis);
            for (const p of ports) {
              if (p > maxPort) maxPort = p;
            }
          }
        }
        if (node.rawTomlOverride) {
          const ports = extractPortsFromString(node.rawTomlOverride);
          for (const p of ports) {
            if (p > maxPort) maxPort = p;
          }
        }
      }

      if (maxPort <= 0) {
        return { basePort: 11010, wgPort: 11011, isIncremented: false, maxFoundPort: null };
      }

      let nextBase = maxPort + 1;
      if (nextBase > 65534) {
        nextBase = 11010;
      }
      const nextWg = nextBase + 1;
      return { basePort: nextBase, wgPort: nextWg, isIncremented: true, maxFoundPort: maxPort };
    }

    function getNodePortSummary(node) {
      if (!node) return '';
      const ports = [];
      if (Array.isArray(node.listeners)) {
        for (const l of node.listeners) {
          ports.push(...extractPortsFromString(l));
        }
      }
      const unique = [...new Set(ports)].sort((a, b) => a - b);
      if (unique.length === 0) return '';
      return unique.join(', ');
    }

    const DEFAULT_LISTENERS = buildListenersText(11010, 11011, 'all_dual_stack');

    const nodeForm = reactive({
      instanceName: '',
      networkName: 'default',
      networkSecret: '',
      virtualIpv4: '',
      enableDhcp: true,
      listenersText: DEFAULT_LISTENERS,
      peersText: '',
      proxyNetworksText: '',
      routesText: '',
      rawTomlOverride: '',
      autoStart: true,
    });

    // 抽屉：运行时配置热打补丁（零丢包）
    const showPatchDrawer = ref(false);
    const patchNode = ref(null);
    const patchForm = reactive({
      peersText: '',
      proxyNetworksText: '',
      routesText: '',
    });

    // 模态框：内核管理与程序热升级
    const showEngineModal = ref(false);
    const engineTab = ref('github'); // 'github' | 'local'
    const checkingRelease = ref(false);
    const releaseInfo = ref(null);
    const githubProxy = ref('https://ghproxy.net/');
    const installingGitHub = ref(false);
    const installResult = ref(null);

    const upgrading = ref(false);
    const upgradeFile = ref(null);
    const upgradeResult = ref(null);

    // 统计总览
    const summary = computed(() => {
      let activeCount = 0;
      let totalPeers = 0;
      let directPeers = 0;
      let totalRx = 0;
      let totalTx = 0;

      for (const n of nodes.value) {
        if (n.isRunning) activeCount++;
        totalPeers += n.peerCount || 0;
        directPeers += n.directPeerCount || 0;
        totalRx += n.totalRxBytes || 0;
        totalTx += n.totalTxBytes || 0;
      }

      return {
        totalNodes: nodes.value.length,
        activeCount,
        totalPeers,
        directPeers,
        relayPeers: Math.max(0, totalPeers - directPeers),
        totalRx,
        totalTx,
      };
    });

    async function loadEngineStatus() {
      const res = await http(API.easytier.engineStatus, { method: 'GET', timeoutMs: 10_000 });
      if (res.ok && res.data) {
        engineStatus.value = res.data;
      }
    }

    async function loadNodes() {
      loading.value = true;
      try {
        const res = await http(API.easytier.nodes, { method: 'GET', timeoutMs: 10_000 });
        if (res.ok && Array.isArray(res.data)) {
          nodes.value = res.data;
        }
      } finally {
        loading.value = false;
      }
    }

    let refreshing = false;
    async function refreshAll() {
      if (refreshing) return;
      refreshing = true;
      try {
        await Promise.all([loadNodes(), loadEngineStatus()]);
      } finally {
        refreshing = false;
      }
    }

    const probingPort = ref(false);

    async function probeAvailablePorts(preferredStartPort = null) {
      probingPort.value = true;
      try {
        const start = preferredStartPort || activeBasePort.value || 11010;
        const res = await http(API.easytier.availablePort, {
          method: 'GET',
          params: { startPort: start },
        });
        if (res.ok && res.data) {
          const { basePort, wgPort, skippedOccupiedPorts, suggestedListeners } = res.data;
          activeBasePort.value = basePort;
          activeWgPort.value = wgPort;
          if (Array.isArray(suggestedListeners) && suggestedListeners.length > 0) {
            nodeForm.listenersText = suggestedListeners.join('\n');
          } else {
            nodeForm.listenersText = buildListenersText(basePort, wgPort, 'all_dual_stack');
          }

          if (Array.isArray(skippedOccupiedPorts) && skippedOccupiedPorts.length > 0) {
            portIncrementInfo.value = `已自动规避被占用端口 (${skippedOccupiedPorts.join(', ')})，分配可用端口: ${basePort}/${wgPort}`;
            toast.info(`⚡ 系统已自动规避被占用端口 (${skippedOccupiedPorts.join(', ')})，分配新端口: ${basePort}/${wgPort}`);
          } else {
            portIncrementInfo.value = `端口探测通过 (可用端口: ${basePort}/${wgPort})`;
          }
        }
      } catch (err) {
        console.warn('探测可用端口失败，沿用本地递增计算端口:', err);
      } finally {
        probingPort.value = false;
      }
    }

    async function openCreateNode() {
      isEditNode.value = false;
      editNodeId.value = null;
      activeTab.value = 'wizard';

      const { basePort, wgPort, isIncremented, maxFoundPort } = computeNextNodePorts();
      activeBasePort.value = basePort;
      activeWgPort.value = wgPort;
      portIncrementInfo.value = isIncremented
        ? `基于现有最大端口 (${maxFoundPort}) 自动递增: ${basePort}/${wgPort} (正在探测系统占用...)`
        : '默认初始分配端口: 11010/11011 (正在探测系统占用...)';

      Object.assign(nodeForm, {
        instanceName: 'node_' + Math.floor(Math.random() * 1000),
        networkName: 'default',
        networkSecret: '',
        virtualIpv4: '',
        enableDhcp: true,
        listenersText: buildListenersText(basePort, wgPort, 'all_dual_stack'),
        peersText: 'tcp://public.easytier.top:11010',
        proxyNetworksText: '',
        routesText: '',
        rawTomlOverride: '',
        autoStart: true,
      });
      showNodeModal.value = true;

      await probeAvailablePorts(basePort);
    }

    function applyListenerPreset(type) {
      if (type === 'none') {
        nodeForm.listenersText = '';
        toast.info('已切换为【纯客户端模式】（不监听本地端口）');
        return;
      }

      const existingPorts = extractPortsFromString(nodeForm.listenersText);
      let basePort = activeBasePort.value || 11010;
      let wgPort = activeWgPort.value || (basePort + 1);
      if (existingPorts.length > 0) {
        basePort = existingPorts[0];
        wgPort = existingPorts.length > 1 ? existingPorts[1] : (basePort + 1);
      }

      nodeForm.listenersText = buildListenersText(basePort, wgPort, type);
      if (type === 'all_dual_stack') {
        toast.info(`已应用【全协议双栈】预设 (TCP/UDP: ${basePort}, WG: ${wgPort})`);
      } else if (type === 'all_ipv4') {
        toast.info(`已应用【仅 IPv4 (TCP+UDP+WG)】预设 (端口: ${basePort}/${wgPort})`);
      } else if (type === 'udp_dual_stack') {
        toast.info(`已应用【双栈 UDP 纯打洞直连】预设 (端口: ${basePort})`);
      } else if (type === 'tcp_udp_dual') {
        toast.info(`已应用【双栈 TCP + UDP (无WG)】预设 (端口: ${basePort})`);
      }
    }

    function applyPeerPreset(type) {
      if (type === 'public_tcp') {
        nodeForm.peersText = 'tcp://public.easytier.top:11010';
        toast.info('已填入【官方公共 TCP 中继】');
      } else if (type === 'public_udp') {
        nodeForm.peersText = 'udp://public.easytier.top:11010';
        toast.info('已填入【官方公共 UDP 中继】');
      } else if (type === 'worker_wss') {
        nodeForm.peersText = 'wss://your-domain.workers.dev/';
        toast.info('已填入【Cloudflare Worker WSS 中继】示例');
      }
    }

    async function openEditNode(node) {
      isEditNode.value = true;
      editNodeId.value = node.id;
      activeTab.value = 'wizard';
      portIncrementInfo.value = '';

      const res = await http(API.easytier.nodeItem(node.id), { method: 'GET' });
      if (res.ok && res.data) {
        const detail = res.data;
        const cfg = detail.config;
        const existingPorts = extractPortsFromString((cfg.listeners || []).join('\n'));
        if (existingPorts.length > 0) {
          activeBasePort.value = existingPorts[0];
          activeWgPort.value = existingPorts.length > 1 ? existingPorts[1] : (existingPorts[0] + 1);
        } else {
          activeBasePort.value = 11010;
          activeWgPort.value = 11011;
        }

        Object.assign(nodeForm, {
          instanceName: cfg.instanceName,
          networkName: cfg.networkName,
          networkSecret: cfg.networkSecret || '',
          virtualIpv4: cfg.virtualIpv4 || '',
          enableDhcp: cfg.enableDhcp,
          listenersText: (cfg.listeners || []).join('\n'),
          peersText: (cfg.peers || []).join('\n'),
          proxyNetworksText: (cfg.proxyNetworks || []).join('\n'),
          routesText: (cfg.routes || []).join('\n'),
          rawTomlOverride: cfg.rawTomlOverride || '',
          autoStart: cfg.autoStart,
        });
      }
      showNodeModal.value = true;
    }

    async function saveNode() {
      if (!nodeForm.instanceName.trim()) return toast.error('请输入实例名称');
      if (!nodeForm.networkName.trim()) return toast.error('请输入虚拟网络名称');

      saving.value = true;
      const payload = {
        instanceName: nodeForm.instanceName.trim(),
        networkName: nodeForm.networkName.trim(),
        networkSecret: nodeForm.networkSecret.trim() || null,
        virtualIpv4: nodeForm.virtualIpv4.trim() || null,
        enableDhcp: nodeForm.enableDhcp && !nodeForm.virtualIpv4.trim(),
        listeners: splitLines(nodeForm.listenersText),
        peers: splitLines(nodeForm.peersText),
        proxyNetworks: splitLines(nodeForm.proxyNetworksText),
        routes: splitLines(nodeForm.routesText),
        rawTomlOverride: nodeForm.rawTomlOverride.trim() || null,
        autoStart: nodeForm.autoStart,
      };

      const res = isEditNode.value
        ? await http(API.easytier.nodeItem(editNodeId.value), { method: 'PUT', body: payload })
        : await http(API.easytier.nodes, { method: 'POST', body: payload });

      saving.value = false;
      if (res.ok) {
        toast.success(isEditNode.value ? '节点配置已更新' : 'EasyTier 节点已创建');
        showNodeModal.value = false;
        refreshAll();
      } else {
        toast.error(res.data?.message || '保存失败');
      }
    }

    async function deleteNode(node) {
      if (!confirm(`确定要永久删除节点 "${node.instanceName}" (${node.networkName}) 吗？`)) return;
      const res = await http(API.easytier.nodeItem(node.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('节点已删除');
        refreshAll();
      } else {
        toast.error(res.data?.message || '删除失败');
      }
    }

    async function startNode(node) {
      const res = await http(API.easytier.nodeStart(node.id), { method: 'POST' });
      if (res.ok) {
        toast.success(`节点 "${node.instanceName}" 启动成功`);
        refreshAll();
      } else {
        toast.error(res.data?.message || '启动失败');
      }
    }

    async function stopNode(node) {
      const res = await http(API.easytier.nodeStop(node.id), { method: 'POST' });
      if (res.ok) {
        toast.info(`节点 "${node.instanceName}" 已停止`);
        refreshAll();
      } else {
        toast.error(res.data?.message || '停止失败');
      }
    }

    async function viewNodeTopology(node) {
      if (activeNodeDetail.value && activeNodeDetail.value.config.id === node.id) {
        activeNodeDetail.value = null;
        return;
      }
      const res = await http(API.easytier.nodeItem(node.id), { method: 'GET' });
      if (res.ok && res.data) {
        activeNodeDetail.value = res.data;
      } else {
        toast.error(res.data?.message || '获取拓扑失败');
      }
    }

    // 打开运行时热打补丁
    async function openHotPatch(node) {
      patchNode.value = node;
      const res = await http(API.easytier.nodeItem(node.id), { method: 'GET' });
      if (res.ok && res.data) {
        const cfg = res.data.config;
        patchForm.peersText = (cfg.peers || []).join('\n');
        patchForm.proxyNetworksText = (cfg.proxyNetworks || []).join('\n');
        patchForm.routesText = (cfg.routes || []).join('\n');
      }
      showPatchDrawer.value = true;
    }

    async function submitHotPatch() {
      if (!patchNode.value) return;
      saving.value = true;
      const payload = {
        connectorsToAdd: splitLines(patchForm.peersText),
        connectorsToRemove: null,
        proxyNetworks: splitLines(patchForm.proxyNetworksText),
        routes: splitLines(patchForm.routesText),
        hostname: patchNode.value.instanceName,
        disableRelayData: null,
        preferPeerRelay: null,
      };

      const res = await http(API.easytier.patchConfig(patchNode.value.id), { method: 'POST', body: payload });
      saving.value = false;
      if (res.ok) {
        toast.success('⚡ 原地热补丁生效！网络零中断，连接已更新');
        showPatchDrawer.value = false;
        refreshAll();
      } else {
        toast.error(res.data?.message || '热补丁应用失败');
      }
    }

    // 内核文件上传与平滑升级
    function handleFileChange(event) {
      const file = event?.target?.files?.[0];
      if (file) upgradeFile.value = file;
    }

    async function submitEngineUpgrade() {
      if (!upgradeFile.value) return toast.error('请选择要上传的原生动态库文件 (easytier_ffi.dll / .so)');
      upgrading.value = true;
      upgradeResult.value = null;

      const res = await httpUpload(API.easytier.engineUpgrade, { file: upgradeFile.value });
      upgrading.value = false;
      if (res.ok) {
        upgradeResult.value = res.data;
        toast.success('EasyTier 内核平滑升级完成！');
        refreshAll();
      } else {
        toast.error(res.data?.message || '内核升级失败');
      }
    }

    async function checkGitHubRelease() {
      checkingRelease.value = true;
      releaseInfo.value = null;
      installResult.value = null;
      const res = await http(API.easytier.githubRelease, {
        method: 'GET',
        params: { proxyPrefix: githubProxy.value },
      });
      checkingRelease.value = false;
      if (res.ok && res.data) {
        releaseInfo.value = res.data;
        if (res.data.hasUpdate) {
          toast.info(`检测到 EasyTier 官方新版本: ${res.data.tagName}`);
        } else {
          toast.success(`当前已是最新版本 (${res.data.tagName})`);
        }
      } else {
        toast.error(res.data?.message || '检查 GitHub 发布失败');
      }
    }

    async function installFromGitHub() {
      if (!confirm(`确定要从 GitHub 下载并安装 EasyTier ${releaseInfo.value?.tagName || '最新版'} 内核吗？\n升级过程中将排空旧实例并在安装后自动恢复。`)) return;

      installingGitHub.value = true;
      installResult.value = null;
      const payload = {
        tagName: releaseInfo.value?.tagName || null,
        proxyPrefix: githubProxy.value || null,
      };

      const res = await http(API.easytier.installGitHub, { method: 'POST', body: payload });
      installingGitHub.value = false;
      if (res.ok && res.data) {
        installResult.value = res.data;
        toast.success('EasyTier 内核自动安装与热更新完成！');
        refreshAll();
      } else {
        toast.error(res.data?.message || '从 GitHub 下载安装失败');
      }
    }

    function splitLines(text) {
      if (!text) return [];
      return text.split('\n').map((s) => s.trim()).filter(Boolean);
    }

    onMounted(() => {
      refreshAll();
      refreshTimer = setInterval(refreshAll, 6000);
    });

    onUnmounted(() => {
      if (refreshTimer) clearInterval(refreshTimer);
    });

    return {
      nodes,
      loading,
      saving,
      engineStatus,
      activeNodeDetail,
      showNodeModal,
      isEditNode,
      activeTab,
      nodeForm,
      showPatchDrawer,
      patchNode,
      patchForm,
      showEngineModal,
      engineTab,
      checkingRelease,
      releaseInfo,
      githubProxy,
      installingGitHub,
      installResult,
      checkGitHubRelease,
      installFromGitHub,
      upgrading,
      upgradeFile,
      upgradeResult,
      summary,
      refreshAll,
      openCreateNode,
      openEditNode,
      saveNode,
      deleteNode,
      startNode,
      stopNode,
      viewNodeTopology,
      openHotPatch,
      submitHotPatch,
      applyListenerPreset,
      applyPeerPreset,
      handleFileChange,
      submitEngineUpgrade,
      formatBytes,
      portIncrementInfo,
      getNodePortSummary,
      activeBasePort,
      activeWgPort,
      probingPort,
      probeAvailablePorts,
    };
  },
  template: `
    <div class="flex flex-col gap-5">
      <!-- 顶部标题与操作栏 -->
      <div class="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold text-slate-100 flex items-center gap-2">
            EasyTier 虚拟组网管理
            <span class="badge border-emerald-500/40 text-emerald-400 bg-emerald-950/30">P2P Mesh Overlay</span>
          </h1>
          <p class="text-xs text-slate-400 mt-1">
            基于 Rust 原生核心驱动的点对点去中心化虚拟局域网。支持全互联拓扑、NAT 打洞直连、运行时配置零丢包热打补丁与内核无感热升级。
          </p>
        </div>
        <div class="flex items-center gap-2 flex-wrap">
          <button class="btn btn-xs" @click="refreshAll()">
            <span>刷新状态</span>
          </button>
          <button class="btn btn-xs" @click="showEngineModal = true">
            <span>⚙️ 内核管理</span>
          </button>
          <button class="btn btn-xs btn-primary" @click="openCreateNode()">
            <span>+ 新建网络节点</span>
          </button>
        </div>
      </div>

      <!-- 核心指标大盘 -->
      <div class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">活跃节点 / 总数</div>
          <div class="text-lg font-bold text-slate-100 mt-1">
            <span class="text-emerald-400">{{ summary.activeCount }}</span> / {{ summary.totalNodes }}
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5">本地运行实例</div>
        </div>

        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">P2P 直连对端</div>
          <div class="text-lg font-bold text-cyan-400 mt-1">
            {{ summary.directPeers }} <span class="text-xs font-normal text-slate-400">个</span>
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5">STUN NAT 成功穿透</div>
        </div>

        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">中继节点 (Relay)</div>
          <div class="text-lg font-bold text-amber-400 mt-1">
            {{ summary.relayPeers }} <span class="text-xs font-normal text-slate-400">个</span>
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5">多跳路径保活转发</div>
        </div>

        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">总对端连接 (Peers)</div>
          <div class="text-lg font-bold text-indigo-400 mt-1">
            {{ summary.totalPeers }} <span class="text-xs font-normal text-slate-400">节点</span>
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5">Full-Mesh 拓扑规模</div>
        </div>

        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">下行总流量 (RX)</div>
          <div class="text-lg font-bold text-slate-200 mt-1">
            {{ formatBytes(summary.totalRx) }}
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5">虚拟网络入站字节</div>
        </div>

        <div class="panel p-3 flex flex-col justify-between">
          <div class="text-[11px] font-medium text-slate-400">内核就绪状态</div>
          <div class="text-sm font-semibold mt-1 flex items-center gap-1.5" :class="engineStatus?.isInstalled ? 'text-emerald-400' : 'text-rose-400'">
            <span class="inline-block w-2 h-2 rounded-full" :class="engineStatus?.isInstalled ? 'bg-emerald-400 animate-pulse' : 'bg-rose-400'"></span>
            {{ engineStatus?.isInstalled ? (engineStatus?.version || '就绪') : '未安装' }}
          </div>
          <div class="text-[10px] text-slate-500 mt-0.5 truncate" :title="engineStatus?.nativeLibraryPath">
            {{ engineStatus?.mode || 'Native FFI' }}
          </div>
        </div>
      </div>

      <!-- 系统运行权限提醒横幅 (Windows 非管理员 / Linux 非 root) -->
      <div v-if="engineStatus && !engineStatus.hasAdminPrivilege" class="p-3 rounded-lg bg-amber-950/40 border border-amber-500/50 flex items-start gap-3 text-xs text-amber-200">
        <span class="text-base leading-none">⚠️</span>
        <div class="flex-1">
          <div class="font-bold text-amber-300 mb-0.5">系统运行权限提示：未获得管理员/特权身份</div>
          <div class="text-amber-200/90 leading-relaxed">{{ engineStatus.privilegeWarning }}</div>
        </div>
      </div>

      <!-- 节点列表 -->
      <div v-if="nodes.length === 0" class="panel p-12 text-center text-slate-400">
        <div class="text-4xl mb-3">🌐</div>
        <div class="text-base font-medium text-slate-200">暂无 EasyTier 虚拟网络节点</div>
        <div class="text-xs text-slate-400 mt-1 max-w-md mx-auto">
          点击右上角“+ 新建网络节点”，填入网络名称与密码，即可一键加入或创建专属去中心化 P2P 虚拟局域网。
        </div>
        <button class="btn btn-primary text-xs mt-4" @click="openCreateNode()">
          立即创建首个节点
        </button>
      </div>

      <div v-else class="grid grid-cols-1 gap-4">
        <div v-for="node in nodes" :key="node.id" class="panel p-4 transition-all hover:border-slate-600">
          <div class="flex flex-col md:flex-row md:items-center justify-between gap-3 pb-3 border-b border-slate-700/60">
            <div class="flex items-center gap-3">
              <span class="relative flex h-3 w-3">
                <span v-if="node.isRunning" class="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75"></span>
                <span class="relative inline-flex rounded-full h-3 w-3" :class="node.isRunning ? 'bg-emerald-500' : 'bg-slate-600'"></span>
              </span>
              <div>
                <div class="flex items-center gap-2">
                  <span class="text-base font-bold text-slate-100">{{ node.instanceName }}</span>
                  <span class="badge border-slate-700 text-slate-300 bg-slate-800/80 text-[11px]">
                    网段: {{ node.networkName }}
                  </span>
                  <span v-if="node.deviceName" class="text-xs font-mono text-cyan-400 bg-cyan-950/40 px-1.5 py-0.5 rounded border border-cyan-800/50">
                    网卡: {{ node.deviceName }}
                  </span>
                </div>
                <div class="flex items-center gap-4 text-xs text-slate-400 mt-1 flex-wrap">
                  <span>虚拟 IP: 
                    <strong v-if="node.virtualIpv4" class="text-emerald-400 font-mono">{{ node.virtualIpv4 }}</strong>
                    <span v-else class="text-amber-400 font-mono" :title="node.lastError || '正在等待对端连接与 DHCP 协商分配'">
                      {{ node.lastError ? '待就绪 (见下方提示)' : 'DHCP 分配中...' }}
                    </span>
                  </span>
                  <span>对端: <strong class="text-cyan-400">{{ node.peerCount }}</strong> 节点 (直连: {{ node.directPeerCount }})</span>
                  <span>流量: ↓{{ formatBytes(node.totalRxBytes) }} / ↑{{ formatBytes(node.totalTxBytes) }}</span>
                  <span v-if="getNodePortSummary(node)">
                    监听端口: <span class="font-mono text-cyan-300 font-semibold">{{ getNodePortSummary(node) }}</span>
                  </span>
                </div>
                <div v-if="node.lastError" class="mt-2 text-xs text-rose-300 bg-rose-950/40 border border-rose-800/60 rounded px-2 py-1 flex items-center gap-1.5">
                  <span class="text-rose-400 font-bold">⚠️ 提示:</span>
                  <span>{{ node.lastError }}</span>
                </div>
              </div>
            </div>

            <!-- 操作按钮组 -->
            <div class="flex items-center gap-1.5 flex-wrap">
              <button
                v-if="!node.isRunning"
                class="btn btn-xs btn-primary"
                @click="startNode(node)">
                启动节点
              </button>
              <button
                v-else
                class="btn btn-xs btn-danger"
                @click="stopNode(node)">
                停止节点
              </button>

              <button
                v-if="node.isRunning"
                class="btn btn-xs"
                title="零断网在线添加对端或路由"
                @click="openHotPatch(node)">
                ⚡ 快捷热配
              </button>

              <button
                class="btn btn-xs"
                @click="viewNodeTopology(node)">
                {{ activeNodeDetail && activeNodeDetail.config.id === node.id ? '收起详情' : '拓扑明细' }}
              </button>

              <button
                class="btn btn-xs"
                @click="openEditNode(node)">
                编辑
              </button>

              <button
                class="btn btn-xs btn-danger"
                @click="deleteNode(node)">
                删除
              </button>
            </div>
          </div>

          <!-- 展开的拓扑详情抽屉/面板 -->
          <div v-if="activeNodeDetail && activeNodeDetail.config.id === node.id" class="mt-4 pt-3 border-t border-slate-700/60 flex flex-col gap-3">
            <div class="grid grid-cols-1 md:grid-cols-2 gap-3 text-xs bg-slate-900/60 p-3 rounded border border-slate-800">
              <div>
                <span class="text-slate-400">物理监听:</span>
                <span class="text-slate-200 font-mono ml-1">{{ (activeNodeDetail.activeListeners || []).join(', ') || '未开启监听 (纯客户端出站模式)' }}</span>
              </div>
              <div>
                <span class="text-slate-400">STUN NAT 诊断:</span>
                <span class="text-emerald-400 font-mono ml-1">{{ activeNodeDetail.stunNatType || '探测中...' }}</span>
              </div>
            </div>

            <!-- 对端 Peer 表格 -->
            <div>
              <div class="text-xs font-semibold text-slate-300 mb-2 flex items-center justify-between">
                <span>在线对端拓扑 ({{ (activeNodeDetail.peers || []).length }} 个 Peer)</span>
                <span class="text-[11px] text-slate-400 font-normal">动态根据 RTT 择优调度路径</span>
              </div>

              <div v-if="!activeNodeDetail.peers || activeNodeDetail.peers.length === 0" class="text-xs text-slate-500 py-4 text-center">
                暂无发现的对端节点，请检查对端 Peer 地址配置是否连通
              </div>

              <div v-else class="overflow-x-auto">
                <table class="w-full text-left text-xs border-collapse">
                  <thead>
                    <tr class="text-slate-400 border-b border-slate-800 bg-slate-900/40">
                      <th class="p-2">节点 ID / 主机名</th>
                      <th class="p-2">虚拟 IP</th>
                      <th class="p-2">链路类型</th>
                      <th class="p-2">传输协议</th>
                      <th class="p-2">NAT 类型</th>
                      <th class="p-2">往返时延 (RTT)</th>
                      <th class="p-2">累计流量</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="p in activeNodeDetail.peers" :key="p.peerId" class="border-b border-slate-800/50 hover:bg-slate-800/30">
                      <td class="p-2 font-mono text-slate-200">
                        <div class="font-medium text-slate-200">{{ p.hostname || '节点' }}</div>
                        <div class="text-[10px] text-slate-500 font-mono">ID: {{ p.peerId }}</div>
                      </td>
                      <td class="p-2 font-mono">
                        <span v-if="p.virtualIpv4" class="text-emerald-400 font-bold bg-emerald-950/50 px-2 py-0.5 rounded border border-emerald-800/60 inline-block">
                          {{ p.virtualIpv4 }}
                        </span>
                        <span v-else class="text-slate-500 text-[11px] italic">
                          -
                        </span>
                      </td>
                      <td class="p-2">
                        <span class="badge" :class="p.connectionType.includes('Direct') ? 'border-cyan-500/40 text-cyan-400 bg-cyan-950/20' : (p.connectionType.includes('Local') ? 'border-emerald-500/40 text-emerald-400 bg-emerald-950/20' : 'border-amber-500/40 text-amber-400 bg-amber-950/20')">
                          {{ p.connectionType }}
                        </span>
                      </td>
                      <td class="p-2 text-slate-300 font-mono">{{ p.protocol }}</td>
                      <td class="p-2 text-slate-400 font-mono text-[11px]">{{ p.natType || p.tunnelAddress || '-' }}</td>
                      <td class="p-2 font-mono" :class="p.connectionType.includes('Local') ? 'text-slate-400' : (p.latencyMs < 50 ? 'text-emerald-400' : (p.latencyMs < 150 ? 'text-amber-400' : 'text-rose-400'))">
                        {{ p.connectionType.includes('Local') ? '< 1 ms (本机)' : (p.latencyMs > 0 ? p.latencyMs + ' ms' : '< 1 ms') }}
                      </td>
                      <td class="p-2 text-slate-400 font-mono text-[11px]">↓{{ formatBytes(p.rxBytes) }} / ↑{{ formatBytes(p.txBytes) }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>

              <!-- 虚拟网段路由表明细 (如果存在) -->
              <div v-if="activeNodeDetail.routes && activeNodeDetail.routes.length > 0" class="mt-3">
                <div class="text-[11px] font-semibold text-slate-400 mb-1.5 flex items-center gap-1.5">
                  <span>虚拟网段路由表 ({{ activeNodeDetail.routes.length }} 条)</span>
                </div>
                <div class="overflow-x-auto">
                  <table class="w-full text-left text-[11px] border-collapse bg-slate-900/30 rounded border border-slate-800/60">
                    <thead>
                      <tr class="text-slate-500 border-b border-slate-800 bg-slate-900/60">
                        <th class="p-1.5">目标网段 / IP</th>
                        <th class="p-1.5">下一跳</th>
                        <th class="p-1.5">跃点数 (Cost)</th>
                        <th class="p-1.5">路径延时</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-for="(r, idx) in activeNodeDetail.routes" :key="idx" class="border-b border-slate-800/40 hover:bg-slate-800/20">
                        <td class="p-1.5 font-mono text-emerald-400 font-semibold">{{ r.destinationCidr }}</td>
                        <td class="p-1.5 font-mono text-slate-300">{{ r.nextHopPeerId }}</td>
                        <td class="p-1.5 font-mono text-slate-400">{{ r.cost }}</td>
                        <td class="p-1.5 font-mono text-cyan-400">{{ r.pathLatencyMs !== '-' ? r.pathLatencyMs + ' ms' : '-' }}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>

            <!-- 生成的 TOML 预览 -->
            <details class="text-xs text-slate-400">
              <summary class="cursor-pointer hover:text-slate-200">查看生成的 EasyTier TOML 配置</summary>
              <pre class="output-block mt-2 font-mono text-[11px] text-emerald-300">{{ activeNodeDetail.generatedToml }}</pre>
            </details>
          </div>
        </div>
      </div>

      <!-- 模态框：创建 / 编辑节点 -->
      <div v-if="showNodeModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEditNode ? '编辑 EasyTier 节点配置' : '新建 EasyTier 网络节点' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showNodeModal = false">✕</button>
          </div>

          <!-- 模式切换 Tab -->
          <div class="flex items-center gap-2 border-b border-slate-800 pb-2">
            <button
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
              :class="activeTab === 'wizard' ? 'bg-cyan-500/15 text-cyan-300 border border-cyan-500/30' : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/40'"
              @click="activeTab = 'wizard'">
              可视化表单向导
            </button>
            <button
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
              :class="activeTab === 'toml' ? 'bg-cyan-500/15 text-cyan-300 border border-cyan-500/30' : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/40'"
              @click="activeTab = 'toml'">
              原生 TOML 高级代码模式
            </button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <!-- 向导模式 -->
            <div v-if="activeTab === 'wizard'" class="flex flex-col gap-3">
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label class="block text-slate-400 mb-1">节点实例名称 (Instance Name) <span class="text-rose-400">*</span></label>
                  <input
                    v-model="nodeForm.instanceName"
                    class="input font-mono"
                    placeholder="如 node_nas, home_gateway"
                    :disabled="isEditNode" />
                </div>
                <div>
                  <label class="block text-slate-400 mb-1">虚拟网络名称 (Network Name) <span class="text-rose-400">*</span></label>
                  <input
                    v-model="nodeForm.networkName"
                    class="input font-mono"
                    placeholder="如 my_vnet, default" />
                </div>
              </div>

              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label class="block text-slate-400 mb-1">网络通信密钥 (Network Secret)</label>
                  <input
                    v-model="nodeForm.networkSecret"
                    type="password"
                    class="input font-mono"
                    placeholder="可选，异地节点间端对端加密密码" />
                </div>
                <div>
                  <label class="block text-slate-400 mb-1">虚拟 IPv4 地址 (可选静态指定)</label>
                  <input
                    v-model="nodeForm.virtualIpv4"
                    class="input font-mono"
                    placeholder="如 10.126.127.1/24 (填写后使用静态 IP)" />
                  <p class="mt-1 text-xs text-slate-500">填写地址后禁用 DHCP；留空并启用 DHCP 才会自动分配。原始 TOML 覆盖配置优先。</p>
                </div>
              </div>

              <div class="border-t border-slate-800/80 pt-2 flex flex-col sm:flex-row items-start sm:items-center gap-4 text-xs">
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer flex-1 w-full">
                  <input type="checkbox" v-model="nodeForm.enableDhcp" :disabled="!!nodeForm.virtualIpv4.trim()" class="accent-cyan-500" />
                  <span class="text-slate-200">{{ nodeForm.virtualIpv4.trim() ? '已指定静态 IP，DHCP 不生效' : '启用 DHCP 自动分配虚拟 IP' }}</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer flex-1 w-full">
                  <input type="checkbox" v-model="nodeForm.autoStart" class="accent-emerald-500" />
                  <span class="text-slate-200">服务启动时自动拉起本节点</span>
                </label>
              </div>

              <div v-if="engineStatus && !engineStatus.hasAdminPrivilege" class="p-2 rounded bg-amber-950/30 border border-amber-800/50 text-[11px] text-amber-300/90 flex items-start gap-1.5">
                <span>⚠️</span>
                <span>提示：当前服务以普通用户权限运行，系统在创建本地 TUN 虚拟网卡时可能会失败。若遇到网卡创建失败，建议以管理员身份重新启动 WebHost 服务。</span>
              </div>

              <div>
                <div class="flex items-center justify-between mb-1">
                  <label class="text-slate-400">对端节点地址列表 (Peers / 每行一个)</label>
                  <div class="flex items-center gap-1 flex-wrap">
                    <span class="text-[10px] text-slate-500">快速填入:</span>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-slate-800 text-slate-300 hover:bg-slate-700"
                      @click="applyPeerPreset('public_tcp')">
                      官方 TCP
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-slate-800 text-slate-300 hover:bg-slate-700"
                      @click="applyPeerPreset('public_udp')">
                      官方 UDP
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-indigo-950/40 text-indigo-300 border border-indigo-800/60 hover:bg-indigo-900/60"
                      @click="applyPeerPreset('worker_wss')">
                      Worker WSS
                    </button>
                  </div>
                </div>
                <textarea
                  v-model="nodeForm.peersText"
                  rows="2"
                  class="input font-mono text-xs"
                  placeholder="tcp://public.easytier.top:11010&#10;udp://public.easytier.top:11010&#10;wss://your-domain.workers.dev/"></textarea>
                <span class="text-[11px] text-slate-500 mt-0.5 block">连接到公共根节点、自建节点或 Cloudflare Worker 即可全网自动发现并建立 P2P 穿透</span>
              </div>

              <div>
                <div class="flex items-center justify-between mb-1">
                  <div class="flex items-center gap-2 flex-wrap">
                    <label class="text-slate-400">本地监听端口 (Listeners / 全协议双栈)</label>
                    <span v-if="portIncrementInfo" class="badge text-[10px] bg-emerald-950/60 text-emerald-300 border-emerald-700/50">
                      ⚡ {{ portIncrementInfo }}
                    </span>
                  </div>
                  <div class="flex items-center gap-1 flex-wrap">
                    <button type="button" class="btn btn-xs py-0.5 px-2 text-[10px] bg-amber-950/40 text-amber-300 border border-amber-700/60 hover:bg-amber-900/60 flex items-center gap-1"
                      :disabled="probingPort"
                      title="向宿主机探测系统空闲端口，自动避开已被占用的端口"
                      @click="probeAvailablePorts(activeBasePort)">
                      <span v-if="probingPort" class="animate-spin text-[10px]">⌛</span>
                      <span v-else>🔍</span>
                      <span>{{ probingPort ? '探测中...' : '探测可用端口' }}</span>
                    </button>
                    <span class="text-[10px] text-slate-500">预设:</span>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-emerald-950/50 text-emerald-300 border border-emerald-700/60 hover:bg-emerald-900/60"
                      @click="applyListenerPreset('all_dual_stack')">
                      🌟 全协议双栈
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-sky-950/40 text-sky-300 border border-sky-800/60 hover:bg-sky-900/60"
                      @click="applyListenerPreset('all_ipv4')">
                      仅 IPv4
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-slate-800 text-slate-300 hover:bg-slate-700"
                      @click="applyListenerPreset('udp_dual_stack')">
                      UDP 双栈
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-slate-800 text-slate-300 hover:bg-slate-700"
                      @click="applyListenerPreset('tcp_udp_dual')">
                      TCP+UDP
                    </button>
                    <button type="button" class="btn btn-xs py-0.5 px-1.5 text-[10px] bg-slate-800 text-slate-400 hover:bg-slate-700"
                      @click="applyListenerPreset('none')">
                      纯客户端(清空)
                    </button>
                  </div>
                </div>
                <textarea
                  v-model="nodeForm.listenersText"
                  rows="4"
                  class="input font-mono text-xs"
                  placeholder="tcp://0.0.0.0:11010&#10;udp://0.0.0.0:11010&#10;wg://0.0.0.0:11011&#10;tcp://[::]:11010&#10;udp://[::]:11010&#10;wg://[::]:11011"></textarea>
                <span class="text-[11px] text-slate-500 mt-0.5 block">
                  💡 <strong>全栈互通优势</strong>：默认开启 <code>tcp</code>、<code>udp</code> 和 <code>wg</code> 的 IPv4 与 IPv6 ([::]) 全量监听。即使在复杂对称 NAT 下，只要双端支持 IPv6 即可直接点对点极速通信，<strong>完全脱离 Cloudflare Worker 中继流量</strong>。
                </span>
              </div>

              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label class="block text-slate-400 mb-1">子网代理网段 (Proxy Networks)</label>
                  <textarea
                    v-model="nodeForm.proxyNetworksText"
                    rows="2"
                    class="input font-mono"
                    placeholder="如 192.168.1.0/24"></textarea>
                  <span class="text-[11px] text-slate-500 mt-0.5 block">打通内网 LAN，其他对端可直接访问该子网</span>
                </div>
                <div>
                  <label class="block text-slate-400 mb-1">静态路由 (Routes)</label>
                  <textarea
                    v-model="nodeForm.routesText"
                    rows="2"
                    class="input font-mono"
                    placeholder="如 10.0.0.0/8"></textarea>
                  <span class="text-[11px] text-slate-500 mt-0.5 block">声明需要由虚拟网络承载的远端路由</span>
                </div>
              </div>
            </div>

            <!-- 原生 TOML 模式 -->
            <div v-else class="flex flex-col gap-2">
              <label class="block text-slate-400 mb-1">原生 EasyTier TOML 配置 (覆盖上方向导)</label>
              <textarea
                v-model="nodeForm.rawTomlOverride"
                rows="12"
                class="input font-mono text-emerald-300 !bg-slate-950/90"
                placeholder="# 若填写将直接使用此 TOML 内容启动实例&#10;inst_name = &quot;my_node&quot;&#10;network_name = &quot;default&quot;&#10;listeners = [&quot;tcp://0.0.0.0:11010&quot;]"></textarea>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showNodeModal = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="saveNode()">
              {{ saving ? '保存中...' : '确认保存' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 抽屉：运行时配置热打补丁 -->
      <div v-if="showPatchDrawer" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <div class="flex items-center gap-2">
              <span class="text-cyan-400 font-bold text-sm">⚡ 运行时配置原地热补丁</span>
              <span class="badge border-cyan-500/30 text-cyan-400 bg-cyan-950/20 text-[10px]">免断网</span>
            </div>
            <button class="text-slate-400 hover:text-slate-200" @click="showPatchDrawer = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div class="bg-cyan-950/30 border border-cyan-800/40 p-2.5 rounded text-cyan-300 text-[11px] leading-relaxed">
              💡 <strong>零断网原理</strong>：此操作直接调用 EasyTier <code>PatchConfig</code> RPC 总线注入补丁。无需注销虚拟网卡设备，长连接不断开，瞬间完成对端连接与代理规则同步！
            </div>

            <div>
              <label class="block text-slate-400 mb-1">动态对端节点 (Connectors / 每行一个)</label>
              <textarea
                v-model="patchForm.peersText"
                rows="3"
                class="input font-mono"
                placeholder="tcp://192.168.10.5:11010"></textarea>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">动态子网代理网段 (Proxy Networks)</label>
              <textarea
                v-model="patchForm.proxyNetworksText"
                rows="2"
                class="input font-mono"
                placeholder="192.168.2.0/24"></textarea>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">动态路由 (Routes)</label>
              <textarea
                v-model="patchForm.routesText"
                rows="2"
                class="input font-mono"
                placeholder="172.16.0.0/16"></textarea>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showPatchDrawer = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="submitHotPatch()">
              {{ saving ? '应用中...' : '立即热生效' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 模态框：内核管理与平滑热升级 -->
      <div v-if="showEngineModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-xl p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base flex items-center gap-2">
              ⚙️ EasyTier 内核引擎管理与自动更新
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showEngineModal = false">✕</button>
          </div>

          <!-- 当前宿主内核状态卡片 -->
          <div class="bg-slate-900/80 p-3 rounded border border-slate-800 flex flex-col gap-2 text-xs">
            <div class="flex justify-between items-center">
              <span class="text-slate-400">当前版本:</span>
              <span class="text-emerald-400 font-bold font-mono">{{ engineStatus?.version || '未安装' }}</span>
            </div>
            <div class="flex justify-between items-center">
              <span class="text-slate-400">调用模式:</span>
              <span class="text-slate-200 font-mono">{{ engineStatus?.mode || '未就绪' }}</span>
            </div>
            <div class="flex justify-between items-center">
              <span class="text-slate-400">宿主进程 PID:</span>
              <span class="text-slate-200 font-mono">{{ engineStatus?.processId }}</span>
            </div>
            <div class="flex justify-between items-center">
              <span class="text-slate-400">核心程序路径:</span>
              <span class="text-slate-400 font-mono truncate max-w-[300px]" :title="engineStatus?.nativeLibraryPath">{{ engineStatus?.nativeLibraryPath }}</span>
            </div>
            <div class="flex justify-between items-center">
              <span class="text-slate-400">持久化存储根目录:</span>
              <span class="text-emerald-400/90 font-mono truncate max-w-[300px]" :title="engineStatus?.storageDirectory">{{ engineStatus?.storageDirectory }}</span>
            </div>
          </div>

          <!-- 模式切换 Tab -->
          <div class="flex items-center gap-2 border-b border-slate-800 pb-2">
            <button
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
              :class="engineTab === 'github' ? 'bg-cyan-500/15 text-cyan-300 border border-cyan-500/30' : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/40'"
              @click="engineTab = 'github'">
              ⚡ GitHub 官方云端拉取更新
            </button>
            <button
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
              :class="engineTab === 'local' ? 'bg-cyan-500/15 text-cyan-300 border border-cyan-500/30' : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/40'"
              @click="engineTab = 'local'">
              📦 本地文件上传升级
            </button>
          </div>

          <!-- Tab 1: GitHub 官方云端拉取 -->
          <div v-if="engineTab === 'github'" class="flex flex-col gap-3 text-xs">
            <div class="text-slate-400 leading-relaxed text-[11px]">
              🚀 <strong>一键云端自动更新</strong>：直接连接 EasyTier 官方 GitHub Releases 仓库，自动匹配当前操作系统与 CPU 架构下载发布包并完成解压与热重载。
            </div>

            <!-- 加速镜像选择 -->
            <div>
              <label class="block text-slate-400 mb-1">GitHub 网络加速节点</label>
              <select v-model="githubProxy" class="input font-mono text-xs">
                <option value="https://ghproxy.net/">ghproxy.net 代理加速 (国内推荐)</option>
                <option value="https://ghfast.top/">ghfast.top 代理加速 (国内备用)</option>
                <option value="">GitHub 官方直连 (海外/无需代理)</option>
              </select>
            </div>

            <!-- 检查与操作栏 -->
            <div class="flex items-center gap-2">
              <button
                class="btn btn-xs btn-primary flex-1 py-2"
                :disabled="checkingRelease || installingGitHub"
                @click="checkGitHubRelease()">
                {{ checkingRelease ? '正在连接 GitHub 检测...' : '🔍 检查 GitHub 官方版本' }}
              </button>
            </div>

            <!-- 检测结果展示卡片 -->
            <div v-if="releaseInfo" class="bg-slate-900/90 border border-slate-800 rounded p-3 flex flex-col gap-2.5">
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-2">
                  <span class="font-bold text-slate-100 font-mono text-sm">{{ releaseInfo.tagName }}</span>
                  <span v-if="releaseInfo.hasUpdate" class="badge border-amber-500/40 text-amber-300 bg-amber-950/30 text-[10px]">
                    可更新
                  </span>
                  <span v-else class="badge border-emerald-500/40 text-emerald-300 bg-emerald-950/30 text-[10px]">
                    当前最新
                  </span>
                </div>
                <span class="text-slate-500 text-[11px]">{{ new Date(releaseInfo.publishedAt).toLocaleDateString() }}</span>
              </div>

              <div class="text-[11px] text-slate-400 flex flex-col gap-1 bg-slate-950/60 p-2 rounded font-mono">
                <div>匹配架构包: <strong class="text-cyan-300">{{ releaseInfo.matchingAssetFileName }}</strong></div>
                <div>压缩包大小: <strong class="text-slate-200">{{ formatBytes(releaseInfo.matchingAssetSize) }}</strong></div>
              </div>

              <div class="pt-1">
                <button
                  class="btn btn-primary w-full py-2 text-xs"
                  :disabled="installingGitHub"
                  @click="installFromGitHub()">
                  <span v-if="installingGitHub" class="animate-pulse">⏳ 正在从 GitHub 下载并解压安装内核...</span>
                  <span v-else>⬇️ 一键拉取并自动热升级内核</span>
                </button>
              </div>
            </div>

            <div v-if="installResult" class="p-3 bg-emerald-950/40 border border-emerald-800/50 rounded text-emerald-300 text-xs">
              ✅ {{ installResult.message }} (恢复节点: {{ installResult.restoredNodeCount }} 个)
            </div>
          </div>

          <!-- Tab 2: 本地文件上传升级 -->
          <div v-else class="flex flex-col gap-3 text-xs">
            <div class="text-slate-400 leading-relaxed text-[11px]">
              📁 <strong>本地手动升级</strong>：支持从 GitHub 下载的官方 <code>.zip</code> 压缩包，或者编译产物 <code>easytier-core.exe</code>、<code>easytier_ffi.dll</code>、<code>libeasytier_ffi.so</code> 上传。
              宿主 Supervisor 将自动执行解压覆盖与实例热恢复。
            </div>

            <div class="border border-dashed border-slate-700 rounded p-4 text-center hover:border-cyan-500/60 transition-colors bg-slate-950/40">
              <input type="file" @change="handleFileChange($event)" accept=".zip,.dll,.so,.exe" class="text-xs text-slate-300" />
            </div>

            <div v-if="upgradeResult" class="p-3 bg-emerald-950/40 border border-emerald-800/50 rounded text-emerald-300 text-xs">
              ✅ {{ upgradeResult.message }} (恢复节点: {{ upgradeResult.restoredNodeCount }} 个)
            </div>

            <div class="flex items-center justify-end gap-2 pt-2">
              <button
                class="btn btn-primary text-xs"
                :disabled="upgrading || !upgradeFile"
                @click="submitEngineUpgrade()">
                {{ upgrading ? '升级进行中...' : '执行平滑热升级' }}
              </button>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showEngineModal = false">关闭窗口</button>
          </div>
        </div>
      </div>
    </div>
  `,
});


