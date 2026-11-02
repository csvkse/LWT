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
  },
  setup() {
    const plugins = ref([]);
    const loading = ref(false);
    const profileTab = ref('all'); // 'all' | 'resident' | 'oneshot'
    const categoryFilter = ref('all');
    const stateFilter = ref('all'); // 'all' | 'running' | 'degraded' | 'missing' | 'stopped'
    const searchKeyword = ref('');
    const isSafeMode = ref(false);
    const safeModeMessage = ref('');

    // 模态弹窗状态
    const showCreateModal = ref(false);
    const showImportModal = ref(false);
    const showActionModal = ref(false);
    const showWebviewModal = ref(false);
    const showCodeModal = ref(false);
    const showLogsDrawer = ref(false);
    const showKvDrawer = ref(false);
    const showGuideModal = ref(false);
    const showPortSwitchModal = ref(false);
    const selectedGuidePlugin = ref(null);
    const envInfo = ref({});

    const activePlugin = ref(null);
    const activeActionDef = ref(null);

    // 统计指标
    const stats = computed(() => {
      const list = plugins.value;
      const total = list.length;
      const resident = list.filter((p) => p.profile === 0).length;
      const oneshot = list.filter((p) => p.profile === 1).length;
      const running = list.filter((p) => p.profile === 0 && p.state === 2).length;
      const degraded = list.filter((p) => p.state === 3 || p.state === 4 || p.state === 5).length;
      return { total, resident, oneshot, running, degraded };
    });

    // 分类清单
    const categories = computed(() => {
      const set = new Set();
      for (const p of plugins.value) {
        if (p.manifest?.category) {
          set.add(p.manifest.category);
        }
      }
      return Array.from(set);
    });

    // 列表筛选（采用策略模式解耦 Profile、Category、Keyword、State 多维过滤）
    const filteredPlugins = computed(() => {
      return filterPlugins(plugins.value, {
        profile: profileTab.value,
        category: categoryFilter.value,
        keyword: searchKeyword.value,
        state: stateFilter.value,
      });
    });

    async function loadPlugins() {
      loading.value = true;
      try {
        // 全量拉取插件事实源，保证统计指标与分类集合的准确性，过滤完全交由前端策略引擎
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
      searchKeyword.value = '';
    }

    function filterByTotal() {
      profileTab.value = 'all';
      stateFilter.value = 'all';
    }

    function filterByRunning() {
      profileTab.value = 'all';
      stateFilter.value = 'running';
    }

    function filterByOneshot() {
      profileTab.value = 'oneshot';
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
        // 环境信息非关键依赖，异常时静默降级
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
      selectedGuidePlugin,
      envInfo,
      activePlugin,
      activeActionDef,
      stats,
      categories,
      filteredPlugins,
      stateFilter,
      filterByTotal,
      filterByRunning,
      filterByOneshot,
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
      handlePortSwitched,
    };
  },
  template: `
    <div class="space-y-4">
      <!-- 纯净/安全模式运维通告 (Safe Mode Alert Banner) -->
      <div
        v-if="isSafeMode"
        class="panel p-3 border-l-4 border-l-amber-500 bg-amber-950/30 border border-amber-500/30 rounded-lg flex items-start justify-between gap-3 text-xs"
      >
        <div class="flex items-start gap-2 text-amber-200">
          <span class="text-base leading-none">🛡️</span>
          <div>
            <div class="font-bold text-amber-300">系统已进入纯净安全模式 (Safe Mode)</div>
            <div class="text-slate-300 mt-0.5">
              {{ safeModeMessage || '已通过环境变量 (LWT_SAFE_MODE / LWT_PLUGINS_SAFE_MODE) 禁用微服务开机自启与崩溃自愈，便于排查隔离故障插件。' }}
            </div>
          </div>
        </div>
        <span class="px-2 py-0.5 rounded text-[10px] font-mono font-semibold bg-amber-500/20 text-amber-300 border border-amber-500/40 shrink-0">
          SAFE_MODE_ACTIVE
        </span>
      </div>

      <!-- 顶部统计全景卡片（支持点击快捷过滤） -->
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
          title="点击快速筛选运行中的微服务"
          @click="filterByRunning()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">常驻微服务 (运行中)</div>
            <div class="text-xl font-bold text-amber-300 mt-0.5">
              {{ stats.running }} / <span class="text-slate-400 text-sm">{{ stats.resident }}</span>
            </div>
          </div>
          <span class="text-2xl text-amber-400">⚡</span>
        </div>

        <div
          class="panel p-3 border-l-4 border-l-sky-500 flex items-center justify-between cursor-pointer hover:border-sky-400/60 transition-all select-none"
          title="点击展示轻量脚本扩展"
          @click="filterByOneshot()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">轻量脚本扩展</div>
            <div class="text-xl font-bold text-sky-300 mt-0.5">{{ stats.oneshot }}</div>
          </div>
          <span class="text-2xl text-sky-400">🔌</span>
        </div>

        <div
          class="panel p-3 border-l-4 border-l-red-500 flex items-center justify-between cursor-pointer hover:border-red-400/60 transition-all select-none"
          title="点击快速定位降级与异常插件"
          @click="filterByDegraded()"
        >
          <div>
            <div class="text-[11px] text-slate-400 font-medium">异常 / 降级服务</div>
            <div class="text-xl font-bold text-red-400 mt-0.5">{{ stats.degraded }}</div>
          </div>
          <span class="text-2xl text-red-400">⚠️</span>
        </div>
      </div>

      <!-- 核心控制区：导航标签页 + 检索与操作工具栏 (响应式流式通栏排版，彻底杜绝孤岛与断行) -->
      <div class="panel p-3 flex flex-col xl:flex-row items-stretch xl:items-center justify-between gap-3">
        <!-- 标签导航切换 -->
        <div class="flex items-center gap-1 w-full xl:w-auto overflow-x-auto bg-slate-900/80 p-1 rounded-lg border border-cyber-line/50 shrink-0">
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
            :class="profileTab === 'all' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'all'"
          >
            🧩 全部插件 ({{ stats.total }})
          </button>
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
            :class="profileTab === 'resident' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'resident'"
          >
            ⚡ 常驻微服务与守护 ({{ stats.resident }})
          </button>
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
            :class="profileTab === 'oneshot' ? 'bg-sky-500/20 text-sky-300 border border-sky-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'oneshot'"
          >
            🔌 脚本轻量扩展 ({{ stats.oneshot }})
          </button>
        </div>

        <!-- 检索与工具栏按钮 (成组横向单行排版，禁止内部折行断层) -->
        <div class="flex items-center gap-2 w-full xl:w-auto justify-between xl:justify-end overflow-x-auto min-w-0 flex-nowrap shrink-0 py-0.5">
          <!-- 筛选与搜索组合区 -->
          <div class="flex items-center gap-2 shrink-0 flex-nowrap">
            <!-- 运行状态快捷过滤 -->
            <select
              v-model="stateFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-28 sm:w-32"
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

            <!-- 分类筛选 -->
            <select
              v-if="categories.length > 0"
              v-model="categoryFilter"
              class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
            >
              <option value="all">全部分类</option>
              <option v-for="cat in categories" :key="cat" :value="cat">
                {{ cat }}
              </option>
            </select>

            <!-- 搜索框：暗黑科技底色、发光边框、放大镜图标与一键清空 (✕) -->
            <div class="relative flex items-center shrink-0 w-36 sm:w-44 md:w-52">
              <span class="absolute left-2.5 text-slate-500 pointer-events-none text-xs">🔍</span>
              <input
                v-model="searchKeyword"
                type="text"
                class="input text-xs py-1.5 pl-7 pr-7 w-full bg-slate-900/90 border border-cyber-line/80 focus:border-cyan-400 focus:ring-1 focus:ring-cyan-400/30 text-slate-100 placeholder:text-slate-500 rounded-md transition-all"
                placeholder="搜索插件名称/ID..."
                @keydown.esc="searchKeyword = ''"
              />
              <button
                v-if="searchKeyword"
                type="button"
                class="absolute right-2 text-slate-400 hover:text-slate-200 text-xs p-0.5 rounded transition-colors"
                title="清空搜索"
                @click="searchKeyword = ''"
              >
                ✕
              </button>
            </div>
          </div>

          <!-- 导入与新建与刷新 -->
          <div class="flex items-center gap-1.5 shrink-0 flex-nowrap">
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
      </div>

      <!-- 插件卡片网格列表 -->
      <div v-if="loading && plugins.length === 0" class="panel py-16 text-center text-slate-400 text-xs">
        <div class="animate-spin text-2xl mb-2">⏳</div>
        <div>加载插件中枢清单中...</div>
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
    </div>
  `,
});
