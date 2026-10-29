import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { formatTime } from '../utils/format.js';

export default defineComponent({
  name: 'ApiKeysView',
  setup() {
    const keys = ref([]);
    const availableHosts = ref([]);
    const mcpCatalog = ref([]);
    const loading = ref(false);

    // 折叠状态字典：{ [categoryKey]: boolean } (true 为折叠收起)
    const collapsedCategories = reactive({});

    // 新建 / 编辑弹窗
    const showModal = ref(false);
    const isEdit = ref(false);
    const isClone = ref(false);
    const clonedFromName = ref('');
    const currentId = ref(null);
    const form = reactive({
      name: '',
      allowApi: true,
      allowMcp: true,
      allowTerminal: true,
      allowSchedules: true,
      allowFiles: true,
      allowTranscode: false,
      allowGateway: false,
      allowRemoteHosts: false,
      hostScopeMode: 'all', // 'all' | 'custom'
      selectedHostIds: [],
      mcpFilterMode: 'all', // 'all' | 'custom'
      allowedTools: [],     // string[]
      expiresAt: '',
    });

    // 新建成功明文密钥弹窗（仅展示一次）
    const showSecretModal = ref(false);
    const createdKeyInfo = reactive({
      name: '',
      rawKey: '',
    });

    // 快捷客户端配置弹窗
    const showGuideModal = ref(false);
    const guideKey = ref('');

    function parseHostIds(allowedHostIds) {
      if (!allowedHostIds || allowedHostIds === '*') return [];
      try {
        const parsed = JSON.parse(allowedHostIds);
        if (Array.isArray(parsed)) return parsed;
      } catch {
        return allowedHostIds.split(',').map(s => s.trim()).filter(Boolean);
      }
      return [];
    }

    function getAllCatalogToolNames() {
      const names = [];
      for (const cat of mcpCatalog.value) {
        if (cat.tools) {
          for (const t of cat.tools) {
            names.push(t.name);
          }
        }
      }
      return names;
    }

    async function load() {
      loading.value = true;
      const [resKeys, resHosts, resCatalog] = await Promise.all([
        http(API.apiKeys.list, { method: 'GET' }),
        http(API.remoteHosts.list, { method: 'GET' }),
        http(API.apiKeys.mcpCatalog, { method: 'GET' }),
      ]);
      loading.value = false;
      if (resKeys.ok) {
        keys.value = resKeys.data || [];
      }
      if (resHosts.ok) {
        availableHosts.value = resHosts.data || [];
      }
      if (resCatalog.ok) {
        mcpCatalog.value = resCatalog.data || [];
      }
    }

    function openCreate() {
      isEdit.value = false;
      isClone.value = false;
      clonedFromName.value = '';
      currentId.value = null;
      Object.assign(form, {
        name: '',
        allowApi: true,
        allowMcp: true,
        allowTerminal: true,
        allowSchedules: true,
        allowFiles: true,
        allowTranscode: false,
        allowGateway: false,
        allowRemoteHosts: false,
        hostScopeMode: 'all',
        selectedHostIds: [],
        mcpFilterMode: 'all',
        allowedTools: getAllCatalogToolNames(),
        expiresAt: '',
      });
      showModal.value = true;
    }

    function openEdit(key) {
      isEdit.value = true;
      isClone.value = false;
      clonedFromName.value = '';
      currentId.value = key.id;
      const hostIds = parseHostIds(key.allowedHostIds);
      const hasCustomTools = key.allowedTools && Array.isArray(key.allowedTools) && key.allowedTools.length > 0;
      Object.assign(form, {
        name: key.name,
        allowApi: key.allowApi,
        allowMcp: key.allowMcp,
        allowTerminal: key.allowTerminal,
        allowSchedules: key.allowSchedules,
        allowFiles: key.allowFiles,
        allowTranscode: key.allowTranscode,
        allowGateway: key.allowGateway,
        allowRemoteHosts: Boolean(key.allowRemoteHosts),
        hostScopeMode: (key.allowedHostIds && key.allowedHostIds !== '*') ? 'custom' : 'all',
        selectedHostIds: hostIds,
        mcpFilterMode: hasCustomTools ? 'custom' : 'all',
        allowedTools: hasCustomTools ? [...key.allowedTools] : getAllCatalogToolNames(),
        expiresAt: key.expiresAt ? key.expiresAt.slice(0, 16) : '',
      });
      showModal.value = true;
    }

    function openClone(key) {
      isEdit.value = false;
      isClone.value = true;
      clonedFromName.value = key.name;
      currentId.value = key.id;
      const hostIds = parseHostIds(key.allowedHostIds);
      const hasCustomTools = key.allowedTools && Array.isArray(key.allowedTools) && key.allowedTools.length > 0;
      Object.assign(form, {
        name: `${key.name} - 副本`,
        allowApi: key.allowApi,
        allowMcp: key.allowMcp,
        allowTerminal: key.allowTerminal,
        allowSchedules: key.allowSchedules,
        allowFiles: key.allowFiles,
        allowTranscode: key.allowTranscode,
        allowGateway: key.allowGateway,
        allowRemoteHosts: Boolean(key.allowRemoteHosts),
        hostScopeMode: (key.allowedHostIds && key.allowedHostIds !== '*') ? 'custom' : 'all',
        selectedHostIds: hostIds,
        mcpFilterMode: hasCustomTools ? 'custom' : 'all',
        allowedTools: hasCustomTools ? [...key.allowedTools] : getAllCatalogToolNames(),
        expiresAt: key.expiresAt ? key.expiresAt.slice(0, 16) : '',
      });
      showModal.value = true;
    }

    // 分类折叠切换
    function toggleCategoryCollapse(catKey) {
      collapsedCategories[catKey] = !collapsedCategories[catKey];
    }

    // 目录三态勾选判定
    function isCategoryAllSelected(cat) {
      if (!cat.tools || cat.tools.length === 0) return false;
      return cat.tools.every(t => form.allowedTools.includes(t.name));
    }

    function isCategorySomeSelected(cat) {
      if (!cat.tools || cat.tools.length === 0) return false;
      const count = cat.tools.filter(t => form.allowedTools.includes(t.name)).length;
      return count > 0 && count < cat.tools.length;
    }

    function getCategorySelectedCount(cat) {
      if (!cat.tools) return 0;
      return cat.tools.filter(t => form.allowedTools.includes(t.name)).length;
    }

    function toggleCategorySelection(cat) {
      const allSelected = isCategoryAllSelected(cat);
      const catToolNames = cat.tools ? cat.tools.map(t => t.name) : [];
      if (allSelected) {
        form.allowedTools = form.allowedTools.filter(n => !catToolNames.includes(n));
      } else {
        const set = new Set([...form.allowedTools, ...catToolNames]);
        form.allowedTools = Array.from(set);
      }
    }

    function isToolSelected(toolName) {
      return form.allowedTools.includes(toolName);
    }

    function toggleToolSelection(toolName) {
      const idx = form.allowedTools.indexOf(toolName);
      if (idx >= 0) {
        form.allowedTools.splice(idx, 1);
      } else {
        form.allowedTools.push(toolName);
      }
    }

    // 快捷策略预设
    function selectAllTools() {
      form.allowedTools = getAllCatalogToolNames();
    }

    function selectStandardOps() {
      const names = [];
      for (const cat of mcpCatalog.value) {
        if (cat.tools) {
          for (const t of cat.tools) {
            if (t.riskLevel !== 'Dangerous') {
              names.push(t.name);
            }
          }
        }
      }
      form.allowedTools = names;
    }

    function selectReadOnly() {
      const names = [];
      for (const cat of mcpCatalog.value) {
        if (cat.tools) {
          for (const t of cat.tools) {
            if (t.riskLevel === 'Read') {
              names.push(t.name);
            }
          }
        }
      }
      form.allowedTools = names;
    }

    function clearAllTools() {
      form.allowedTools = [];
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入密钥名称');

      let allowedHostIds = null;
      if (form.allowRemoteHosts) {
        if (form.hostScopeMode === 'custom') {
          if (form.selectedHostIds.length === 0) {
            return toast.error('请至少选择一台被授权的远程主机');
          }
          allowedHostIds = JSON.stringify(form.selectedHostIds);
        } else {
          allowedHostIds = '*';
        }
      }

      let allowedTools = null;
      if (form.allowMcp) {
        if (form.mcpFilterMode === 'custom') {
          if (form.allowedTools.length === 0) {
            return toast.error('请至少勾选一个被授权的 MCP 工具，或切换为允许全部');
          }
          allowedTools = form.allowedTools;
        } else {
          allowedTools = null; // null 表示继承一级模块所有工具
        }
      }

      const payload = {
        name: form.name.trim(),
        allowApi: form.allowApi,
        allowMcp: form.allowMcp,
        allowTerminal: form.allowTerminal,
        allowSchedules: form.allowSchedules,
        allowFiles: form.allowFiles,
        allowTranscode: form.allowTranscode,
        allowGateway: form.allowGateway,
        allowRemoteHosts: form.allowRemoteHosts,
        allowedHostIds: allowedHostIds,
        allowedTools: allowedTools,
        expiresAt: form.expiresAt ? new Date(form.expiresAt).toISOString() : null,
      };

      if (isEdit.value) {
        const res = await http(API.apiKeys.update(currentId.value), {
          method: 'PUT',
          body: { ...payload, isEnabled: true },
        });
        if (res.ok) {
          toast.success('更新成功');
          showModal.value = false;
          load();
        }
      } else {
        const res = await http(API.apiKeys.create, {
          method: 'POST',
          body: payload,
        });
        if (res.ok) {
          showModal.value = false;
          createdKeyInfo.name = res.data.name;
          createdKeyInfo.rawKey = res.data.rawKey;
          showSecretModal.value = true;
          load();
        }
      }
    }

    async function toggleKey(key) {
      const res = await http(API.apiKeys.toggle(key.id), { method: 'POST' });
      if (res.ok) {
        key.isEnabled = !key.isEnabled;
        toast.success(res.data?.message || '状态已切换');
      }
    }

    async function removeKey(key) {
      if (!confirm(`确定要永久删除 API Key "${key.name}" 吗？该操作不可撤销！`)) return;
      const res = await http(API.apiKeys.delete(key.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        load();
      }
    }

    function copyToClipboard(text) {
      navigator.clipboard.writeText(text).then(() => {
        toast.success('已复制到剪贴板');
      });
    }

    function openGuide(key) {
      guideKey.value = key ? key.keyPrefix + '...' : 'lwt_live_your_key_here';
      showGuideModal.value = true;
    }

    function getHostScopeLabel(key) {
      if (!key.allowRemoteHosts) return '';
      if (!key.allowedHostIds || key.allowedHostIds === '*') return '全部主机';
      const ids = parseHostIds(key.allowedHostIds);
      return `指定${ids.length}台`;
    }

    const originUrl = window.location.origin;

    onMounted(load);

    return {
      originUrl,
      keys,
      availableHosts,
      mcpCatalog,
      loading,
      collapsedCategories,
      showModal,
      isEdit,
      isClone,
      clonedFromName,
      form,
      showSecretModal,
      createdKeyInfo,
      showGuideModal,
      guideKey,
      load,
      openCreate,
      openEdit,
      openClone,
      toggleCategoryCollapse,
      isCategoryAllSelected,
      isCategorySomeSelected,
      getCategorySelectedCount,
      toggleCategorySelection,
      isToolSelected,
      toggleToolSelection,
      selectAllTools,
      selectStandardOps,
      selectReadOnly,
      clearAllTools,
      save,
      toggleKey,
      removeKey,
      copyToClipboard,
      openGuide,
      formatTime,
      getHostScopeLabel,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold text-slate-100 flex items-center gap-2">
            API Key 与 MCP 权限矩阵
            <span class="badge border-cyan-500/40 text-cyan-400 bg-cyan-950/30">安全凭据</span>
          </h1>
          <p class="text-xs text-slate-400 mt-1">
            对外提供原子级 API Key 授权机制，支持精细控制 REST API 与 MCP (Model Context Protocol) 协议通道以及二级工具树状独立勾选。
          </p>
        </div>
        <div class="flex items-center gap-2">
          <button class="btn btn-secondary" @click="openGuide(null)">
            <span>客户端集成指引</span>
          </button>
          <button class="btn btn-primary" @click="openCreate()">
            <span>+ 新建 API Key</span>
          </button>
        </div>
      </div>

      <!-- 密钥列表 -->
      <div class="panel p-4 flex flex-col gap-3">
        <div v-if="loading && keys.length === 0" class="text-sm text-slate-400 py-8 text-center">
          加载中...
        </div>
        <div v-else-if="keys.length === 0" class="text-sm text-slate-400 py-12 text-center">
          暂无 API Key，请点击右上角新建。
        </div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-xs border-collapse min-w-[54rem]">
            <thead>
              <tr class="border-b border-slate-700/50 text-slate-400 font-medium">
                <th class="py-2.5 px-3 whitespace-nowrap">名称</th>
                <th class="py-2.5 px-3 whitespace-nowrap">前缀 / 标识</th>
                <th class="py-2.5 px-3 whitespace-nowrap">通道权限</th>
                <th class="py-2.5 px-3 whitespace-nowrap">业务模块权限</th>
                <th class="py-2.5 px-3 whitespace-nowrap">MCP 工具范围</th>
                <th class="py-2.5 px-3 whitespace-nowrap">状态</th>
                <th class="py-2.5 px-3 whitespace-nowrap">最后使用</th>
                <th class="py-2.5 px-3 whitespace-nowrap">过期时间</th>
                <th class="py-2.5 px-3 text-right whitespace-nowrap">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/60">
              <tr v-for="k in keys" :key="k.id" class="hover:bg-slate-800/30 transition-colors">
                <td class="py-2.5 px-3 font-medium text-slate-200">
                  {{ k.name }}
                </td>
                <td class="py-2.5 px-3">
                  <span class="font-mono text-cyan-300 bg-cyan-950/40 px-1.5 py-0.5 rounded border border-cyan-800/40">
                    {{ k.keyPrefix }}...
                  </span>
                </td>
                <td class="py-2.5 px-3">
                  <div class="flex items-center gap-1.5">
                    <span v-if="k.allowApi" class="badge border-cyan-500/40 text-cyan-300 bg-cyan-950/30">API</span>
                    <span v-if="k.allowMcp" class="badge border-purple-500/40 text-purple-300 bg-purple-950/30">MCP</span>
                  </div>
                </td>
                <td class="py-2.5 px-3">
                  <div class="flex flex-wrap items-center gap-1">
                    <span v-if="k.allowTerminal" class="badge border-emerald-500/40 text-emerald-300 bg-emerald-950/30">指令/终端</span>
                    <span v-if="k.allowSchedules" class="badge border-blue-500/40 text-blue-300 bg-blue-950/30">工作流/调度</span>
                    <span v-if="k.allowFiles" class="badge border-amber-500/40 text-amber-300 bg-amber-950/30">文件</span>
                    <span v-if="k.allowTranscode" class="badge border-rose-500/40 text-rose-300 bg-rose-950/30">转码</span>
                    <span v-if="k.allowGateway" class="badge border-indigo-500/40 text-indigo-300 bg-indigo-950/30">网关</span>
                    <span v-if="k.allowRemoteHosts" class="badge border-teal-500/40 text-teal-300 bg-teal-950/30">
                      主机 ({{ getHostScopeLabel(k) }})
                    </span>
                  </div>
                </td>
                <td class="py-2.5 px-3">
                  <span v-if="k.allowMcp && k.allowedTools && k.allowedTools.length > 0" class="badge border-purple-500/40 text-purple-300 bg-purple-950/40 font-mono">
                    已勾选 {{ k.allowedTools.length }} 工具
                  </span>
                  <span v-else-if="k.allowMcp" class="text-slate-400 text-[11px]">
                    全部可用工具
                  </span>
                  <span v-else class="text-slate-600 text-[11px]">
                    未开放
                  </span>
                </td>
                <td class="py-2.5 px-3">
                  <span :class="k.isEnabled ? 'text-emerald-400' : 'text-slate-500'">
                    ● {{ k.isEnabled ? '正常' : '已禁用' }}
                  </span>
                </td>
                <td class="py-2.5 px-3 text-slate-400">
                  {{ k.lastUsedAt ? formatTime(k.lastUsedAt) : '从未使用' }}
                </td>
                <td class="py-2.5 px-3 text-slate-400">
                  {{ k.expiresAt ? formatTime(k.expiresAt) : '永久有效' }}
                </td>
                <td class="py-2.5 px-3 text-right">
                  <div class="flex items-center justify-end gap-1.5">
                    <button class="btn btn-xs" @click="openGuide(k)">集成</button>
                    <button class="btn btn-xs" @click="openEdit(k)">编辑</button>
                    <button class="btn btn-xs text-cyan-400 hover:text-cyan-300" @click="openClone(k)" title="基于此密钥配置克隆新密钥">克隆</button>
                    <button class="btn btn-xs" :class="k.isEnabled ? 'text-amber-400' : 'text-emerald-400'" @click="toggleKey(k)">
                      {{ k.isEnabled ? '禁用' : '启用' }}
                    </button>
                    <button class="btn btn-xs text-rose-400 hover:text-rose-300" @click="removeKey(k)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 新建 / 编辑弹窗 -->
      <div v-if="showModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 flex flex-col gap-4 border-slate-700 max-h-[92vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEdit ? '编辑 API Key 权限' : (isClone ? '克隆 API Key (复制配置)' : '新建 API Key') }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showModal = false">✕</button>
          </div>

          <!-- 克隆提示横幅 -->
          <div v-if="isClone" class="p-2.5 rounded bg-cyan-950/40 border border-cyan-800/40 text-cyan-300 text-xs flex items-center gap-2">
            <span>📋 已自动载入原密钥「<strong class="text-cyan-200">{{ clonedFromName }}</strong>」的全部通道、权限矩阵及细粒度工具授权，可直接保存生成新密钥。</span>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <label class="block text-slate-400 mb-1">密钥名称 <span class="text-rose-400">*</span></label>
              <input v-model="form.name" class="input" placeholder="例如：Claude MCP 本地助手 / 外部运维监控" />
            </div>

            <div>
              <label class="block text-slate-400 mb-1">过期时间（留空表示永久有效）</label>
              <input v-model="form.expiresAt" type="datetime-local" class="input" />
            </div>

            <div class="border-t border-slate-800/80 pt-3">
              <label class="block font-medium text-slate-300 mb-2">协议通道权限 (Channel Scope)</label>
              <div class="grid grid-cols-2 gap-2">
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowApi" class="accent-cyan-500" />
                  <span class="text-slate-200">允许 REST API 访问</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowMcp" class="accent-purple-500" />
                  <span class="text-slate-200">允许 MCP 协议访问</span>
                </label>
              </div>
            </div>

            <div class="border-t border-slate-800/80 pt-3">
              <label class="block font-medium text-slate-300 mb-2">业务模块授权 (Module Matrix)</label>
              <div class="grid grid-cols-2 sm:grid-cols-3 gap-2">
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowTerminal" class="accent-emerald-500" />
                  <span class="text-slate-200">指令库与终端</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowSchedules" class="accent-blue-500" />
                  <span class="text-slate-200">工作流与定时调度</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowFiles" class="accent-amber-500" />
                  <span class="text-slate-200">文件与存储挂载</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowTranscode" class="accent-rose-500" />
                  <span class="text-slate-200">FFmpeg 媒体转码</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowGateway" class="accent-indigo-500" />
                  <span class="text-slate-200">家庭网关与代理</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowRemoteHosts" class="accent-teal-500" />
                  <span class="text-slate-200">远程主机管理</span>
                </label>
              </div>

              <!-- 远程主机自定义范围选择面板 (Resource Scoping) -->
              <div v-if="form.allowRemoteHosts" class="mt-2.5 p-3 bg-slate-950/60 rounded border border-teal-500/30 flex flex-col gap-2.5">
                <div class="flex items-center justify-between">
                  <span class="font-medium text-teal-300">服务器授权范围</span>
                  <div class="flex items-center gap-3">
                    <label class="flex items-center gap-1.5 cursor-pointer">
                      <input type="radio" value="all" v-model="form.hostScopeMode" class="accent-teal-500" />
                      <span class="text-slate-300">允许全部远程主机</span>
                    </label>
                    <label class="flex items-center gap-1.5 cursor-pointer">
                      <input type="radio" value="custom" v-model="form.hostScopeMode" class="accent-teal-500" />
                      <span class="text-slate-300">仅允许指定服务器</span>
                    </label>
                  </div>
                </div>

                <div v-if="form.hostScopeMode === 'custom'" class="flex flex-col gap-2 mt-1">
                  <div v-if="availableHosts.length === 0" class="text-xs text-slate-500 py-1">
                    系统中暂无注册的远程主机，请先在「运维控制 -> 远程主机」中添加。
                  </div>
                  <div v-else class="grid grid-cols-2 gap-1.5 max-h-36 overflow-y-auto pr-1">
                    <label v-for="h in availableHosts" :key="h.id" class="flex items-center gap-2 p-1.5 rounded bg-slate-900 border border-slate-800 hover:border-slate-700 cursor-pointer text-xs">
                      <input type="checkbox" :value="h.id" v-model="form.selectedHostIds" class="accent-teal-500" />
                      <span class="truncate text-slate-200 font-mono">{{ h.name }}</span>
                      <span class="text-slate-500 text-[10px]">({{ h.host }})</span>
                    </label>
                  </div>
                </div>
              </div>
            </div>

            <!-- MCP 二级细粒度工具授权树状结构 (Two-Level Tree Granular Scoping) -->
            <div v-if="form.allowMcp" class="border-t border-slate-800/80 pt-3">
              <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mb-2.5">
                <div>
                  <label class="block font-medium text-purple-300">
                    MCP 二级工具树状独立勾选 (Tool-Level Granular Control)
                  </label>
                  <p class="text-[11px] text-slate-400 mt-0.5">
                    仅向 AI Agent 暴露勾选的工具，减少 Token 上下文消耗并杜绝越权幻觉。
                  </p>
                </div>
                <div class="flex items-center gap-3">
                  <label class="flex items-center gap-1.5 cursor-pointer">
                    <input type="radio" value="all" v-model="form.mcpFilterMode" class="accent-purple-500" />
                    <span class="text-slate-300">全量允许</span>
                  </label>
                  <label class="flex items-center gap-1.5 cursor-pointer">
                    <input type="radio" value="custom" v-model="form.mcpFilterMode" class="accent-purple-500" />
                    <span class="text-purple-300 font-medium">独立勾选细粒度</span>
                  </label>
                </div>
              </div>

              <!-- 细粒度工具勾选面板 -->
              <div v-if="form.mcpFilterMode === 'custom'" class="flex flex-col gap-2.5 p-3 rounded-lg bg-slate-950/70 border border-purple-500/30">
                <!-- 快捷策略栏 -->
                <div class="flex flex-wrap items-center justify-between gap-2 pb-2 border-b border-slate-800/60">
                  <span class="text-[11px] text-slate-400">
                    已选 <strong class="text-purple-300">{{ form.allowedTools.length }}</strong> 项工具
                  </span>
                  <div class="flex items-center gap-1.5">
                    <button type="button" class="btn btn-xs hover:border-purple-500/50" @click="selectAllTools()">全选全部</button>
                    <button type="button" class="btn btn-xs hover:border-sky-500/50 text-sky-400" @click="selectStandardOps()">标准运维(排除高危)</button>
                    <button type="button" class="btn btn-xs hover:border-emerald-500/50 text-emerald-400" @click="selectReadOnly()">只读探测</button>
                    <button type="button" class="btn btn-xs hover:border-slate-600 text-slate-400" @click="clearAllTools()">清空</button>
                  </div>
                </div>

                <!-- 领域分类折叠卡片列表 -->
                <div class="flex flex-col gap-2 max-h-72 overflow-y-auto pr-1">
                  <div v-for="cat in mcpCatalog" :key="cat.key" class="rounded border border-slate-800 bg-slate-900/50 overflow-hidden">
                    <!-- 分类卡片标题头 (可折叠 + 一级全选三态) -->
                    <div class="flex items-center justify-between px-3 py-2 bg-slate-800/40 hover:bg-slate-800/60 cursor-pointer select-none"
                         @click="toggleCategoryCollapse(cat.key)">
                      <div class="flex items-center gap-2" @click.stop>
                        <input type="checkbox"
                               :checked="isCategoryAllSelected(cat)"
                               :indeterminate="isCategorySomeSelected(cat)"
                               @change="toggleCategorySelection(cat)"
                               class="accent-purple-500" />
                        <span class="font-medium text-slate-200">{{ cat.name }}</span>
                        <span class="text-[10px] text-slate-500">
                          ({{ getCategorySelectedCount(cat) }} / {{ cat.tools ? cat.tools.length : 0 }})
                        </span>
                      </div>
                      <div class="flex items-center gap-2 text-slate-400 text-xs">
                        <span class="text-[10px] text-slate-400 hidden sm:inline">{{ cat.description }}</span>
                        <span>{{ collapsedCategories[cat.key] ? '▶' : '▼' }}</span>
                      </div>
                    </div>

                    <!-- 二级具体工具列表 (展开后展示) -->
                    <div v-show="!collapsedCategories[cat.key]" class="p-2.5 grid grid-cols-1 sm:grid-cols-2 gap-2 bg-slate-950/40">
                      <div v-for="t in cat.tools" :key="t.name"
                           @click="toggleToolSelection(t.name)"
                           class="flex items-start gap-2 p-2 rounded border transition-colors cursor-pointer select-none text-xs"
                           :class="isToolSelected(t.name) ? 'bg-purple-950/20 border-purple-500/40' : 'bg-slate-900/40 border-slate-800/70 hover:border-slate-700'">
                        <input type="checkbox"
                               :checked="isToolSelected(t.name)"
                               @change.stop="toggleToolSelection(t.name)"
                               class="accent-purple-500 mt-0.5" />
                        <div class="flex flex-col gap-0.5 flex-1 min-w-0">
                          <div class="flex items-center justify-between gap-1.5">
                            <span class="font-mono font-medium text-purple-200 truncate">{{ t.name }}</span>
                            <span class="badge whitespace-nowrap text-[10px] py-0 px-1"
                                  :class="{
                                    'border-emerald-500/40 text-emerald-300 bg-emerald-950/30': t.riskLevel === 'Read',
                                    'border-sky-500/40 text-sky-300 bg-sky-950/30': t.riskLevel === 'Execute',
                                    'border-amber-500/40 text-amber-300 bg-amber-950/30': t.riskLevel === 'Dangerous'
                                  }">
                              {{ t.riskLevel === 'Read' ? '只读' : (t.riskLevel === 'Execute' ? '执行' : '高危') }}
                            </span>
                          </div>
                          <p class="text-[11px] text-slate-400 line-clamp-1">{{ t.description }}</p>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showModal = false">取消</button>
            <button class="btn btn-primary" @click="save()">
              {{ isClone ? '立即克隆并生成' : (isEdit ? '保存更新' : '保存') }}
            </button>
          </div>
        </div>
      </div>

      <!-- 密钥生成成功单次明文展示弹窗 -->
      <div v-if="showSecretModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4 border-cyan-500/50 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-cyan-300 text-base flex items-center gap-2">
              ✓ API Key 创建成功
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showSecretModal = false">✕</button>
          </div>

          <p class="text-xs text-amber-300 bg-amber-950/40 p-3 rounded border border-amber-600/40 leading-relaxed">
            ⚠️ 请立即复制并妥善保存此密钥！出于零信任安全原则，完整密钥仅在此刻展示一次，系统数据库仅存储单向哈希，后续将无法再次找回。
          </p>

          <div class="flex flex-col gap-1">
            <span class="text-xs text-slate-400 font-medium">密钥名称: {{ createdKeyInfo.name }}</span>
            <div class="flex items-center gap-2 mt-1">
              <input :value="createdKeyInfo.rawKey" readonly class="input font-mono text-cyan-300 select-all" />
              <button class="btn btn-primary" @click="copyToClipboard(createdKeyInfo.rawKey)">复制</button>
            </div>
          </div>

          <div class="flex justify-end pt-2">
            <button class="btn" @click="showSecretModal = false">我已妥善保存并关闭</button>
          </div>
        </div>
      </div>

      <!-- 客户端集成指引弹窗 -->
      <div v-if="showGuideModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 flex flex-col gap-4 border-slate-700 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base flex items-center gap-2">
              客户端接入配置指引
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showGuideModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-4 text-xs leading-relaxed text-slate-300">
            <div>
              <h4 class="font-semibold text-cyan-300 mb-1">1. REST API 调用鉴权</h4>
              <p class="text-slate-400 mb-2">在请求 Header 中附加 <code class="text-slate-200 font-mono">X-Api-Key</code> 或 <code class="text-slate-200 font-mono">Authorization: Bearer &lt;key&gt;</code>：</p>
              <pre class="bg-slate-950 p-2.5 rounded border border-slate-800 font-mono text-[11px] text-slate-300 overflow-x-auto">curl -H "X-Api-Key: {{ guideKey }}" {{ originUrl }}/api/RemoteHosts</pre>
            </div>

            <div>
              <h4 class="font-semibold text-purple-300 mb-1">2. AI MCP Server 集成 (Cursor / Claude Desktop)</h4>
              <p class="text-slate-400 mb-2">在客户端配置中，指定 SSE 握手端点与请求头 Header：</p>
              <pre class="bg-slate-950 p-2.5 rounded border border-slate-800 font-mono text-[11px] text-purple-300 overflow-x-auto">{
  "mcpServers": {
    "linuxwebtool": {
      "url": "{{ originUrl }}/mcp/sse",
      "headers": {
        "X-Api-Key": "{{ guideKey }}"
      }
    }
  }
}</pre>
            </div>
          </div>

          <div class="flex justify-end pt-2 border-t border-slate-700/60">
            <button class="btn" @click="showGuideModal = false">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});