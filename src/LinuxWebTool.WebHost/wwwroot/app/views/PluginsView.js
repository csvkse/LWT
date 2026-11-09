import { computed, defineComponent, onMounted, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { filterPlugins } from '../utils/pluginFilterStrategies.js';

import PluginCard from '../components/plugins/PluginCard.js';
import PluginActionModal from '../components/plugins/PluginActionModal.js';
import PluginWebviewModal from '../components/plugins/PluginWebviewModal.js';
import PluginCodeModal from '../components/plugins/PluginCodeModal.js';
import PluginImportModal from '../components/plugins/PluginImportModal.js';
import PluginLogsDrawer from '../components/plugins/PluginLogsDrawer.js';
import PluginKvDrawer from '../components/plugins/PluginKvDrawer.js';
import PluginCreateModal from '../components/plugins/PluginCreateModal.js';
import PluginInstallGuideModal from '../components/plugins/PluginInstallGuideModal.js';
import PluginPortSwitchModal from '../components/plugins/PluginPortSwitchModal.js';
import PluginInvokeDrawer from '../components/plugins/PluginInvokeDrawer.js';

export default defineComponent({
  name: 'PluginsView',
  components: {
    PluginCard,
    PluginActionModal,
    PluginWebviewModal,
    PluginCodeModal,
    PluginImportModal,
    PluginLogsDrawer,
    PluginKvDrawer,
    PluginCreateModal,
    PluginInstallGuideModal,
    PluginPortSwitchModal,
    PluginInvokeDrawer,
  },
  setup() {
    const plugins = ref([]);
    const loading = ref(false);
    const profileTab = ref('all'); // 'all' | 'service' | 'function' | 'task'
    const categoryFilter = ref('all');
    const stateFilter = ref('all'); // 'all' | 'running' | 'degraded' | 'missing' | 'stopped'
    const lifecycleFilter = ref('all'); // 'all' | 'interpreted' | 'precompiled' | 'sourcecompiled'
    const protocolFilter = ref('all'); // 'all' | 'http' | 'uds' | 'wasmabi' | 'cli'
    const searchKeyword = ref('');
    const isSafeMode = ref(false);
    const safeModeMessage = ref('');

    // 模态弹窗与抽屉状态
    const showCreateModal = ref(false);
    const showImportModal = ref(false);
    const showActionModal = ref(false);
    const showWebviewModal = ref(false);
    const showCodeModal = ref(false);
    const showLogsDrawer = ref(false);
    const showKvDrawer = ref(false);
    const showGuideModal = ref(false);
    const showPortSwitchModal = ref(false);
    const showInvokeDrawer = ref(false);
    const selectedGuidePlugin = ref(null);
    const selectedInvokePlugin = ref(null);
    const envInfo = ref({});

    const activePlugin = ref(null);
    const activeActionDef = ref(null);

    // 三大正交计算工作负载统计指标 (MECE 架构)
    const stats = computed(() => {
      const list = plugins.value;
      const total = list.length;
      const service = list.filter((p) => p.profile === 0).length;
      const task = list.filter((p) => p.profile === 1).length;
      const func = list.filter((p) => p.profile === 2 || p.profile === 3).length;
      const running = list.filter((p) => (p.profile === 0 || p.profile === 2) && p.state === 2).length;
      const degraded = list.filter((p) => p.state === 3 || p.state === 4 || p.state === 5).length;
      return { total, service, task, function: func, running, degraded };
    });

    // 业务分类清单
    const categories = computed(() => {
      const set = new Set();
      for (const p of plugins.value) {
        if (p.manifest?.category) {
          set.add(p.manifest.category);
        }
      }
      return Array.from(set);
    });

    // 多维策略过滤（正交支持 Profile 工作负载、Lifecycle 生命周期、Protocol 通讯协议、Category 领域分类、State 运行状态与全文检索）
    const filteredPlugins = computed(() => {
      return filterPlugins(plugins.value, {
        profile: profileTab.value,
        category: categoryFilter.value,
        keyword: searchKeyword.value,
        state: stateFilter.value,
        lifecycle: lifecycleFilter.value,
        protocol: protocolFilter.value,
      });
    });

    async function loadPlugins() {
      loading.value = true;
      try {
        const res = await http(API.plugins.list('all', ''), { method: 'GET' });
        if (res.ok && Array.isArray(res.data)) {
          plugins.value = res.data;
        } else {
          toast.error(res.message || '获取插件列表失败');
        }
      } catch (err) {
        toast.error('请求插件数据异常: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    function resetFilters() {
      profileTab.value = 'all';
      categoryFilter.value = 'all';
      stateFilter.value = 'all';
      lifecycleFilter.value = 'all';
      protocolFilter.value = 'all';
      searchKeyword.value = '';
    }

    function filterByTotal() {
      profileTab.value = 'all';
      stateFilter.value = 'all';
    }

    function filterByRunning() {
      profileTab.value = 'service';
      stateFilter.value = 'running';
    }

    function filterByTask() {
      profileTab.value = 'task';
      stateFilter.value = 'all';
    }

    function filterByDegraded() {
      profileTab.value = 'all';
      stateFilter.value = 'degraded';
    }

    async function startPlugin(p) {
      try {
        const res = await http(API.plugins.start(p.id), { method: 'POST' });
        if (res.ok) {
          toast.success(`插件 [${p.name}] 正在启动`);
          await loadPlugins();
        } else {
          toast.error(res.message || '启动插件失败');
        }
      } catch (err) {
        toast.error('启动插件异常: ' + err.message);
      }
    }

    async function stopPlugin(p) {
      try {
        const res = await http(API.plugins.stop(p.id), { method: 'POST' });
        if (res.ok) {
          toast.info(`插件 [${p.name}] 已停止`);
          await loadPlugins();
        } else {
          toast.error(res.message || '停止插件失败');
        }
      } catch (err) {
        toast.error('停止插件异常: ' + err.message);
      }
    }

    async function restartPlugin(p) {
      try {
        const res = await http(API.plugins.restart(p.id), { method: 'POST' });
        if (res.ok) {
          toast.success(`插件 [${p.name}] 重启成功`);
          await loadPlugins();
        } else {
          toast.error(res.message || '重启插件失败');
        }
      } catch (err) {
        toast.error('重启插件异常: ' + err.message);
      }
    }

    async function toggleEnabled(p) {
      const nextEnabled = p.manifest?.enabled === false;
      try {
        const res = await http(API.plugins.setEnabled(p.id, nextEnabled), {
          method: 'POST',
        });
        if (res.ok) {
          toast.info(`插件 [${p.name}] 已${nextEnabled ? '启用' : '禁用'}`);
          await loadPlugins();
        } else {
          toast.error(res.message || '切换状态失败');
        }
      } catch (err) {
        toast.error('切换插件状态异常: ' + (err.message || '操作失败'));
      }
    }

    async function deletePlugin(p) {
      if (!window.confirm(`确定要彻底卸载插件 [${p.name}] 吗？对应的数据目录也将被移除。`)) {
        return;
      }
      try {
        const res = await http(API.plugins.delete(p.id), { method: 'DELETE' });
        if (res.ok) {
          toast.success(`插件 [${p.name}] 已成功卸载`);
          await loadPlugins();
        } else {
          toast.error(res.message || '卸载插件失败');
        }
      } catch (err) {
        toast.error('卸载插件异常: ' + err.message);
      }
    }

    function openAction({ plugin, action }) {
      activePlugin.value = plugin;
      activeActionDef.value = action;
      showActionModal.value = true;
    }

    function openWebview(p) {
      activePlugin.value = p;
      showWebviewModal.value = true;
    }

    function openCode(p) {
      activePlugin.value = p;
      showCodeModal.value = true;
    }

    function openLogs(p) {
      activePlugin.value = p;
      showLogsDrawer.value = true;
    }

    function openKv(p) {
      activePlugin.value = p;
      showKvDrawer.value = true;
    }

    function openInstallGuide(p) {
      selectedGuidePlugin.value = p;
      showGuideModal.value = true;
    }

    function openPortSwitch(p) {
      activePlugin.value = p;
      showPortSwitchModal.value = true;
    }

    function openInvokeDrawer(p) {
      selectedInvokePlugin.value = p;
      showInvokeDrawer.value = true;
    }

    async function handlePortSwitched() {
      await loadPlugins();
    }

    async function loadEnvironment() {
      try {
        const res = await http(API.services.environment, { method: 'GET' });
        if (res && res.ok && res.data) {
          envInfo.value = res.data;
        }
      } catch {
        envInfo.value = {};
      }
    }

    async function loadSystemMode() {
      try {
        const res = await http(API.plugins.systemMode, { method: 'GET' });
        if (res && res.ok && res.data) {
          isSafeMode.value = Boolean(res.data.isSafeMode || res.data.isAutoStartDisabled);
          safeModeMessage.value = res.data.message || '';
        }
      } catch {
        // 静默降级
      }
    }

    onMounted(() => {
      loadPlugins();
      loadSystemMode();
      loadEnvironment();
    });

    return {
      plugins,
      loading,
      profileTab,
      categoryFilter,
      stateFilter,
      lifecycleFilter,
      protocolFilter,
      searchKeyword,
      isSafeMode,
      safeModeMessage,
      showCreateModal,
      showImportModal,
      showActionModal,
      showWebviewModal,
      showCodeModal,
      showLogsDrawer,
      showKvDrawer,
      showGuideModal,
      showPortSwitchModal,
      showInvokeDrawer,
      selectedGuidePlugin,
      selectedInvokePlugin,
      envInfo,
      activePlugin,
      activeActionDef,
      stats,
      categories,
      filteredPlugins,
      filterByTotal,
      filterByRunning,
      filterByTask,
      filterByDegraded,
      resetFilters,
      loadPlugins,
      startPlugin,
      stopPlugin,
      restartPlugin,
      toggleEnabled,
      deletePlugin,
      openAction,
      openWebview,
      openCode,
      openLogs,
      openKv,
      openInstallGuide,
      openPortSwitch,
      openInvokeDrawer,
      handlePortSwitched,
    };
  },
  template: `
    <div class="space-y-4">
      <!-- 纯净/安全模式通告 -->
      <div
        v-if="isSafeMode"
        class="panel p-3 border-l-4 border-l-amber-500 bg-amber-950/30 border border-amber-500/30 rounded-lg flex items-start justify-between gap-3 text-xs"
      >
        <div class="flex items-start gap-2 text-amber-200">
          <span class="text-base leading-none">🛡️</span>
          <div>
            <div class="font-bold text-amber-300">系统已进入纯净安全模式 (Safe Mode)</div>
            <div class="text-slate-300 mt-0.5">
              {{ safeModeMessage || '已通过环境变量 (LWT_SAFE_MODE / LWT_PLUGINS_SAFE_MODE) 禁用微服务开机自启与崩溃自愈。' }}
            </div>
          </div>
        </div>
        <span class="px-2 py-0.5 rounded text-[10px] font-mono font-semibold bg-amber-500/20 text-amber-300 border border-amber-500/40 shrink-0">
          SAFE_MODE_ACTIVE
        </span>
      </div>

      <!-- 顶部三大正交工作负载统计全景卡片 -->
      <div class="grid grid-cols-2 md:grid-cols-4 gap-3">
        <div
          class="panel p-3 border-l-4 border-l-cyan-500 flex items-center justify-between cursor-pointer hover:border-cyan-400/60 transition-all select-none"
          title="点击展示全部插件"
          @click="filterByTotal()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">全部插件总数</div>
            <div class="text-xl font-bold text-slate-100 mt-0.5">{{ stats.total }}</div>
          </div>
          <span class="text-2xl text-cyan-400">🧩</span>
        </div>

        <div
          class="panel p-3 border-l-4 border-l-amber-500 flex items-center justify-between cursor-pointer hover:border-amber-400/60 transition-all select-none"
          title="点击展示常驻微服务与守护"
          @click="filterByRunning()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">常驻微服务 (运行中)</div>
            <div class="text-xl font-bold text-amber-300 mt-0.5">
              {{ stats.running }} / <span class="text-slate-400 text-sm">{{ stats.service }}</span>
            </div>
          </div>
          <span class="text-2xl text-amber-400">🌐</span>
        </div>

        <div
          class="panel p-3 border-l-4 border-l-purple-500 flex items-center justify-between cursor-pointer hover:border-purple-400/60 transition-all select-none"
          title="点击展示弹性微函数与计算"
          @click="profileTab = 'function'"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">弹性微函数 (FaaS+Wasm)</div>
            <div class="text-xl font-bold text-purple-300 mt-0.5">{{ stats.function }}</div>
          </div>
          <span class="text-2xl text-purple-400">⚡</span>
        </div>

        <div
          class="panel p-3 border-l-4 border-l-sky-500 flex items-center justify-between cursor-pointer hover:border-sky-400/60 transition-all select-none"
          title="点击展示运维指令与单次任务"
          @click="filterByTask()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">运维指令与任务</div>
            <div class="text-xl font-bold text-sky-300 mt-0.5">{{ stats.task }}</div>
          </div>
          <span class="text-2xl text-sky-400">🛠️</span>
        </div>
      </div>

      <!-- 核心控制区：双层语义解耦控制台 (第 1 层：工作负载 Tabs 与全局操作；第 2 层：检索与多维切面过滤) -->
      <div class="panel p-3 flex flex-col gap-3">
        <!-- 第 1 层：三大正交工作负载范式 Tabs + 全局管理核心动作组 -->
        <div class="flex flex-col sm:flex-row items-stretch sm:items-center justify-between gap-2.5">
          <!-- 标签导航切换：三大正交工作负载范式 (支持小屏横向平滑滑动，隐藏滚动条) -->
          <div class="flex items-center gap-1 overflow-x-auto no-scrollbar bg-slate-900/80 p-1 rounded-lg border border-cyber-line/50 shrink-0">
            <button
              type="button"
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
              :class="profileTab === 'all' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="profileTab = 'all'"
            >
              🧩 全部 ({{ stats.total }})
            </button>
            <button
              type="button"
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
              :class="profileTab === 'service' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="profileTab = 'service'"
            >
              🌐 常驻微服务与网关 ({{ stats.service }})
            </button>
            <button
              type="button"
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
              :class="profileTab === 'function' ? 'bg-purple-500/20 text-purple-300 border border-purple-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="profileTab = 'function'"
            >
              ⚡ 弹性微函数与计算 ({{ stats.function }})
            </button>
            <button
              type="button"
              class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
              :class="profileTab === 'task' ? 'bg-sky-500/20 text-sky-300 border border-sky-500/40' : 'text-slate-400 hover:text-slate-200'"
              @click="profileTab = 'task'"
            >
              🛠️ 运维指令与单次任务 ({{ stats.task }})
            </button>
          </div>

          <!-- 全局操作动作组：透明互调、导入、新建与刷新 (永远显式外露于顶层右侧，杜绝任何截断隐患) -->
          <div class="flex items-center gap-1.5 shrink-0 justify-end flex-wrap">
            <button
              type="button"
              class="btn-secondary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap text-cyan-300"
              title="打开微服务与微函数透明反射互调网关"
              @click="openInvokeDrawer(null)"
            >
              <span>📡</span>
              <span class="hidden sm:inline">透明互调</span>
            </button>
            <button
              type="button"
              class="btn-secondary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap"
              @click="showImportModal = true"
            >
              <span>📦</span>
              <span class="hidden sm:inline">导入/扫描</span>
            </button>
            <button
              type="button"
              class="btn-primary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap"
              @click="showCreateModal = true"
            >
              <span>+</span>
              <span class="hidden sm:inline">新建插件</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1.5 text-slate-400 hover:text-white shrink-0"
              title="刷新列表"
              @click="loadPlugins()"
            >
              <span>🔄</span>
            </button>
          </div>
        </div>

        <!-- 第 2 层：全文检索与多维切面过滤工具栏 (搜索框全宽自适应拉伸，过滤组紧凑对齐) -->
        <div class="flex flex-col md:flex-row items-stretch md:items-center justify-between gap-2.5 pt-2 border-t border-cyber-line/30">
          <!-- 全文检索输入框 (flex-1 弹性填补剩余空间，告别原本拥挤狭窄的固定宽度) -->
          <div class="relative flex-1 min-w-[180px] max-w-xl">
            <span class="absolute left-2.5 top-1/2 -translate-y-1/2 text-slate-500 pointer-events-none text-xs">🔍</span>
            <input
              v-model="searchKeyword"
              type="text"
              class="input text-xs py-1.5 pl-7 pr-7 w-full bg-slate-900/90 border border-cyber-line/80 focus:border-cyan-400 focus:ring-1 focus:ring-cyan-400/30 text-slate-100 placeholder:text-slate-500 rounded-md transition-all"
              placeholder="搜索插件名称、描述、ID 或分类领域..."
              @keydown.esc="searchKeyword = ''"
            />
            <button
              v-if="searchKeyword"
              type="button"
              class="absolute right-2 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-200 text-xs p-0.5 rounded transition-colors"
              title="清空搜索"
              @click="searchKeyword = ''"
            >
              ✕
            </button>
          </div>

          <!-- 多维切面过滤下拉选择组 (生命周期、协议、状态、分类) + 重置按钮 -->
          <div class="flex items-center gap-2 flex-wrap shrink-0">
            <!-- 生命周期切面 -->
            <select
              v-model="lifecycleFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
              title="按生命周期（解释型/预编译/源码构建）过滤"
            >
              <option value="all">全生命周期</option>
              <option value="interpreted">⚡ 解释执行</option>
              <option value="precompiled">📦 预编译模块</option>
              <option value="sourcecompiled">🔨 源码构建</option>
            </select>

            <!-- 协议切面 -->
            <select
              v-model="protocolFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
              title="按通讯协议过滤"
            >
              <option value="all">全通讯协议</option>
              <option value="http">🌐 HTTP</option>
              <option value="uds">🔌 UDS</option>
              <option value="wasmabi">⚡ WasmAbi</option>
              <option value="cli">💻 CLI</option>
            </select>

            <!-- 运行状态过滤 -->
            <select
              v-model="stateFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
              title="按插件当前运行状态过滤"
            >
              <option value="all">全部状态</option>
              <option value="running">🟢 运行中</option>
              <option value="ready">⚡ 待命/就绪</option>
              <option value="stopped">⏹️ 已停止</option>
              <option value="disabled">🚫 已禁用</option>
              <option value="degraded">⚠️ 异常/降级</option>
              <option value="missing">🧩 缺少依赖</option>
            </select>

            <!-- 领域分类筛选 -->
            <select
              v-if="categories.length > 0"
              v-model="categoryFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
              title="按领域分类筛选"
            >
              <option value="all">全部分类</option>
              <option v-for="cat in categories" :key="cat" :value="cat">
                {{ cat }}
              </option>
            </select>

            <!-- 一键重置全部过滤条件按钮 (仅在存在活跃筛选时高亮显示) -->
            <button
              v-if="profileTab !== 'all' || lifecycleFilter !== 'all' || protocolFilter !== 'all' || stateFilter !== 'all' || categoryFilter !== 'all' || searchKeyword"
              type="button"
              class="btn-ghost text-xs py-1 px-2 text-cyan-400 hover:text-cyan-300 hover:bg-cyan-950/40 rounded flex items-center gap-1 transition-colors shrink-0"
              title="重置所有筛选条件"
              @click="resetFilters()"
            >
              <span>↺</span>
              <span class="hidden sm:inline">重置</span>
            </button>
          </div>
        </div>
      </div>

      <!-- 插件卡片网格列表 (加载占位具备视口保底高度与呼吸灯动画，杜绝布局高度断崖抖动) -->
      <div v-if="loading && plugins.length === 0" class="panel min-h-[45vh] flex flex-col items-center justify-center text-slate-400 text-xs space-y-3">
        <div class="relative flex items-center justify-center">
          <div class="w-12 h-12 rounded-full border-2 border-cyan-500/20 border-t-cyan-400 animate-spin"></div>
          <div class="absolute text-lg animate-pulse">🧩</div>
        </div>
        <div class="text-slate-300 font-medium tracking-wide">加载插件中枢清单中...</div>
        <div class="text-slate-500 text-[11px]">正在探活微服务与解析拓扑网络...</div>
      </div>

      <div v-else-if="filteredPlugins.length === 0" class="panel py-16 text-center text-slate-400 text-xs space-y-3">
        <div class="text-3xl">🧩</div>
        <div class="text-slate-300 font-medium">未找到符合条件的插件</div>
        <div class="text-slate-500">
          当前检索条件无匹配项，可尝试
          <button
            type="button"
            class="text-cyan-400 hover:text-cyan-300 underline ml-1 cursor-pointer"
            @click="resetFilters()"
          >
            重置所有筛选
          </button>
          或点击“导入/扫描”添加官方与社区插件
        </div>
      </div>

      <div v-else class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 list-viewport p-1">
        <PluginCard
          v-for="p in filteredPlugins"
          :key="p.id"
          :plugin="p"
          @start="startPlugin(p)"
          @stop="stopPlugin(p)"
          @restart="restartPlugin(p)"
          @toggle-enabled="toggleEnabled(p)"
          @delete="deletePlugin(p)"
          @action="openAction($event)"
          @webview="openWebview(p)"
          @code="openCode(p)"
          @logs="openLogs(p)"
          @kv="openKv(p)"
          @install-guide="openInstallGuide(p)"
          @switch-port="openPortSwitch(p)"
          @invoke="openInvokeDrawer(p)"
        />
      </div>

      <!-- 弹出模态层 -->
      <PluginInstallGuideModal
        :show="showGuideModal"
        :plugin="selectedGuidePlugin"
        :env="envInfo"
        @close="showGuideModal = false"
      />

      <PluginActionModal
        :show="showActionModal"
        :plugin="activePlugin"
        :action-def="activeActionDef"
        @close="showActionModal = false"
        @refresh="loadPlugins()"
      />

      <PluginWebviewModal
        :show="showWebviewModal"
        :plugin="activePlugin"
        @close="showWebviewModal = false"
      />

      <PluginPortSwitchModal
        :show="showPortSwitchModal"
        :plugin="activePlugin"
        @close="showPortSwitchModal = false"
        @switched="handlePortSwitched"
      />

      <PluginCodeModal
        :show="showCodeModal"
        :plugin="activePlugin"
        @close="showCodeModal = false"
        @saved="loadPlugins()"
      />

      <PluginImportModal
        :show="showImportModal"
        @close="showImportModal = false"
        @imported="loadPlugins()"
      />

      <PluginLogsDrawer
        :show="showLogsDrawer"
        :plugin="activePlugin"
        @close="showLogsDrawer = false"
      />

      <PluginKvDrawer
        :show="showKvDrawer"
        :plugin="activePlugin"
        @close="showKvDrawer = false"
      />

      <PluginCreateModal
        :show="showCreateModal"
        @close="showCreateModal = false"
        @created="loadPlugins()"
      />

      <!-- 微服务与微函数动态发现与透明调用抽屉 -->
      <PluginInvokeDrawer
        :show="showInvokeDrawer"
        :plugin="selectedInvokePlugin"
        @close="showInvokeDrawer = false"
      />
    </div>
  `,
});
