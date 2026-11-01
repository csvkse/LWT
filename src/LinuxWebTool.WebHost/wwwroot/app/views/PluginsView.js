import { computed, defineComponent, onMounted, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

import PluginCard from '../components/plugins/PluginCard.js';
import PluginActionModal from '../components/plugins/PluginActionModal.js';
import PluginWebviewModal from '../components/plugins/PluginWebviewModal.js';
import PluginCodeModal from '../components/plugins/PluginCodeModal.js';
import PluginImportModal from '../components/plugins/PluginImportModal.js';
import PluginLogsDrawer from '../components/plugins/PluginLogsDrawer.js';
import PluginKvDrawer from '../components/plugins/PluginKvDrawer.js';
import PluginCreateModal from '../components/plugins/PluginCreateModal.js';

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
  },
  setup() {
    const plugins = ref([]);
    const loading = ref(false);
    const profileTab = ref('all'); // 'all' | 'resident' | 'oneshot'
    const categoryFilter = ref('all');
    const searchKeyword = ref('');

    // 模态弹窗状态
    const showCreateModal = ref(false);
    const showImportModal = ref(false);
    const showActionModal = ref(false);
    const showWebviewModal = ref(false);
    const showCodeModal = ref(false);
    const showLogsDrawer = ref(false);
    const showKvDrawer = ref(false);

    const activePlugin = ref(null);
    const activeActionDef = ref(null);

    // 统计指标
    const stats = computed(() => {
      const list = plugins.value;
      const total = list.length;
      const resident = list.filter((p) => p.profile === 0).length;
      const oneshot = list.filter((p) => p.profile === 1).length;
      const running = list.filter((p) => p.profile === 0 && p.state === 2).length;
      const degraded = list.filter((p) => p.state === 4 || p.state === 5).length;
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

    // 列表筛选
    const filteredPlugins = computed(() => {
      return plugins.value.filter((p) => {
        // Tab 过滤
        if (profileTab.value === 'resident' && p.profile !== 0) return false;
        if (profileTab.value === 'oneshot' && p.profile !== 1) return false;

        // 分类过滤
        if (categoryFilter.value !== 'all' && p.manifest?.category !== categoryFilter.value) {
          return false;
        }

        // 关键词过滤
        if (searchKeyword.value.trim()) {
          const kw = searchKeyword.value.trim().toLowerCase();
          const matchName = (p.name || '').toLowerCase().includes(kw);
          const matchId = (p.id || '').toLowerCase().includes(kw);
          const matchDesc = (p.manifest?.description || '').toLowerCase().includes(kw);
          const matchCat = (p.manifest?.category || '').toLowerCase().includes(kw);
          if (!matchName && !matchId && !matchDesc && !matchCat) return false;
        }

        return true;
      });
    });

    async function loadPlugins() {
      loading.value = true;
      try {
        const res = await http(API.plugins.list(profileTab.value, searchKeyword.value), { method: 'GET' });
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
        const res = await http(API.plugins.setEnabled(p.id), {
          method: 'POST',
        });
        if (res.ok) {
          toast.info(`插件 [${p.name}] 已${nextEnabled ? '启用' : '禁用'}`);
          await loadPlugins();
        } else {
          toast.error(res.message || '切换状态失败');
        }
      } catch (err) {
        toast.error('切换插件状态异常: ' + err.message);
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

    onMounted(() => {
      loadPlugins();
    });

    return {
      plugins,
      loading,
      profileTab,
      categoryFilter,
      searchKeyword,
      showCreateModal,
      showImportModal,
      showActionModal,
      showWebviewModal,
      showCodeModal,
      showLogsDrawer,
      showKvDrawer,
      activePlugin,
      activeActionDef,
      stats,
      categories,
      filteredPlugins,
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
    };
  },
  template: `
    <div class="space-y-4">
      <!-- 顶部统计全景卡片 -->
      <div class="grid grid-cols-2 md:grid-cols-4 gap-3">
        <div class="panel p-3 border-l-4 border-l-cyan-500 flex items-center justify-between">
          <div>
            <div class="text-[11px] text-slate-400 font-medium">全部插件总数</div>
            <div class="text-xl font-bold text-slate-100 mt-0.5">{{ stats.total }}</div>
          </div>
          <span class="text-2xl text-cyan-400">🧩</span>
        </div>

        <div class="panel p-3 border-l-4 border-l-amber-500 flex items-center justify-between">
          <div>
            <div class="text-[11px] text-slate-400 font-medium">常驻微服务 (运行中)</div>
            <div class="text-xl font-bold text-amber-300 mt-0.5">
              {{ stats.running }} / <span class="text-slate-400 text-sm">{{ stats.resident }}</span>
            </div>
          </div>
          <span class="text-2xl text-amber-400">⚡</span>
        </div>

        <div class="panel p-3 border-l-4 border-l-sky-500 flex items-center justify-between">
          <div>
            <div class="text-[11px] text-slate-400 font-medium">轻量脚本扩展</div>
            <div class="text-xl font-bold text-sky-300 mt-0.5">{{ stats.oneshot }}</div>
          </div>
          <span class="text-2xl text-sky-400">🔌</span>
        </div>

        <div class="panel p-3 border-l-4 border-l-red-500 flex items-center justify-between">
          <div>
            <div class="text-[11px] text-slate-400 font-medium">异常 / 降级服务</div>
            <div class="text-xl font-bold text-red-400 mt-0.5">{{ stats.degraded }}</div>
          </div>
          <span class="text-2xl text-red-400">⚠️</span>
        </div>
      </div>

      <!-- 核心控制区：导航标签页 + 检索与操作工具栏 -->
      <div class="panel p-3 flex flex-col md:flex-row items-center justify-between gap-3">
        <!-- 标签导航切换 -->
        <div class="flex items-center gap-1 w-full md:w-auto bg-slate-900/80 p-1 rounded-lg border border-cyber-line/50">
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
            :class="profileTab === 'all' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'all'"
          >
            🧩 全部插件 ({{ stats.total }})
          </button>
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
            :class="profileTab === 'resident' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'resident'"
          >
            ⚡ 常驻微服务与守护 ({{ stats.resident }})
          </button>
          <button
            type="button"
            class="px-3 py-1.5 rounded text-xs font-medium transition-colors"
            :class="profileTab === 'oneshot' ? 'bg-sky-500/20 text-sky-300 border border-sky-500/40' : 'text-slate-400 hover:text-slate-200'"
            @click="profileTab = 'oneshot'"
          >
            🔌 脚本轻量扩展 ({{ stats.oneshot }})
          </button>
        </div>

        <!-- 检索与工具栏按钮 -->
        <div class="flex items-center gap-2 w-full md:w-auto justify-end">
          <!-- 分类筛选 -->
          <select
            v-if="categories.length > 0"
            v-model="categoryFilter"
            class="input-base text-xs py-1.5 px-2 bg-slate-900"
          >
            <option value="all">全部分类</option>
            <option v-for="cat in categories" :key="cat" :value="cat">
              {{ cat }}
            </option>
          </select>

          <!-- 搜索框 -->
          <input
            v-model="searchKeyword"
            type="text"
            class="input-base text-xs py-1.5 px-3 w-40 md:w-48"
            placeholder="搜索插件名称/ID..."
          />

          <!-- 导入与新建 -->
          <button
            type="button"
            class="btn-secondary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0"
            @click="showImportModal = true"
          >
            <span>📦</span>
            <span class="hidden sm:inline">导入/扫描</span>
          </button>
          <button
            type="button"
            class="btn-primary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0"
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

      <!-- 插件卡片网格列表 -->
      <div v-if="loading && plugins.length === 0" class="panel py-16 text-center text-slate-400 text-xs">
        <div class="animate-spin text-2xl mb-2">⏳</div>
        <div>加载插件中枢清单中...</div>
      </div>

      <div v-else-if="filteredPlugins.length === 0" class="panel py-16 text-center text-slate-400 text-xs space-y-3">
        <div class="text-3xl">🧩</div>
        <div class="text-slate-300 font-medium">未找到符合条件的插件</div>
        <div class="text-slate-500">尝试更换筛选标签或点击“导入/扫描”添加官方与社区插件</div>
      </div>

      <div v-else class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
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
        />
      </div>

      <!-- 弹出模态层 -->
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
