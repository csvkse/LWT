import { defineComponent, onMounted, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';
import SambaUsersPanel from './SambaUsersPanel.js';
import SambaShareModal from './SambaShareModal.js';
import SambaSessionsPanel from './SambaSessionsPanel.js';

export default defineComponent({
  name: 'SambaSubmodule',
  components: {
    SambaUsersPanel,
    SambaShareModal,
    SambaSessionsPanel,
  },
  setup() {
    const loading = ref(false);
    const saving = ref(false);
    const installing = ref(false);
    const isInstalled = ref(true);
    const isRunning = ref(false);
    const isHostPierced = ref(false);
    const workgroup = ref('WORKGROUP');
    const shares = ref([]);
    const users = ref([]);
    const sessions = ref([]);
    const activeTab = ref('shares'); // 'shares' | 'users' | 'sessions'
    const usersPanelRef = ref(null);
    const showShareModal = ref(false);
    const editingShare = ref(null);

    function formatBytes(bytes) {
      if (!bytes || bytes <= 0) return null;
      const gb = bytes / (1024 * 1024 * 1024);
      if (gb >= 1) return gb.toFixed(1) + ' GB';
      const mb = bytes / (1024 * 1024);
      return mb.toFixed(1) + ' MB';
    }

    async function loadConfig() {
      loading.value = true;
      try {
        const res = await http(API.services.sambaConfig, { method: 'GET' });
        if (res.ok && res.data) {
          isInstalled.value = res.data.isInstalled ?? true;
          isRunning.value = res.data.isRunning ?? false;
          isHostPierced.value = res.data.isHostPierced ?? false;
          workgroup.value = res.data.workgroup || 'WORKGROUP';
          shares.value = res.data.shares || [];
          users.value = res.data.users || [];
          sessions.value = res.data.sessions || [];
        } else {
          toast.error(res.message || '加载 Samba 配置失败');
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function installSambaOnHost() {
      installing.value = true;
      toast.info('正在宿主机静默安装 Samba 套件，请稍候...');
      try {
        const res = await http(API.services.sambaInstall, { method: 'POST' });
        if (res.ok && res.data?.success) {
          toast.success(res.data.message || 'Samba 服务在宿主机安装成功并已启动');
          await loadConfig();
        } else {
          toast.error(res.data?.message || res.message || '安装失败');
        }
      } catch (err) {
        toast.error('安装请求异常: ' + err.message);
      } finally {
        installing.value = false;
      }
    }

    function openCreateShare() {
      editingShare.value = null;
      showShareModal.value = true;
    }

    function openEditShare(s) {
      editingShare.value = { ...s };
      showShareModal.value = true;
    }

    async function handleSaveShare(payload) {
      saving.value = true;
      try {
        let res;
        if (payload.isEdit) {
          res = await http(`${API.services.sambaShares}/${encodeURIComponent(payload.originalName)}`, {
            method: 'PUT',
            body: payload,
          });
        } else {
          res = await http(API.services.sambaShares, {
            method: 'POST',
            body: payload,
          });
        }

        if (res.ok && res.data?.success) {
          toast.success(res.data.message || 'Samba 共享保存成功');
          showShareModal.value = false;
          await loadConfig();
        } else {
          toast.error(res.data?.message || res.message || '操作失败');
        }
      } catch (err) {
        toast.error('请求异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    async function removeShare(shareName) {
      if (!confirm(`确定删除共享 [${shareName}] 吗？此操作仅从 smb.conf 注销共享，不会删除物理文件。`)) return;
      saving.value = true;
      try {
        const res = await http(`${API.services.sambaShares}/${encodeURIComponent(shareName)}`, {
          method: 'DELETE',
        });
        if (res.ok && res.data?.success) {
          toast.success(res.data.message || `共享 [${shareName}] 已删除`);
          await loadConfig();
        } else {
          toast.error(res.data?.message || res.message || '删除失败');
        }
      } catch (err) {
        toast.error('删除异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    function triggerAddUser() {
      if (usersPanelRef.value) {
        usersPanelRef.value.openAddUserModal();
      }
    }

    onMounted(() => {
      loadConfig();
    });

    return {
      loading,
      saving,
      installing,
      isInstalled,
      isRunning,
      isHostPierced,
      workgroup,
      shares,
      users,
      sessions,
      activeTab,
      usersPanelRef,
      showShareModal,
      editingShare,
      formatBytes,
      loadConfig,
      installSambaOnHost,
      openCreateShare,
      openEditShare,
      handleSaveShare,
      removeShare,
      triggerAddUser,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 宿主未安装 Samba 警告引导栏 -->
      <div v-if="!isInstalled" class="panel p-3.5 border-amber-500/40 bg-amber-950/20 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-2.5">
          <span class="text-xl">⚠️</span>
          <div>
            <div class="font-display font-semibold text-amber-300 text-sm">
              宿主机未检测到 Samba (smbd) 守护服务
            </div>
            <div class="text-xs text-amber-200/70 mt-0.5">
              当前宿主机系统尚未安装 Samba 服务套件，点击右侧按钮可一键在宿主机自动化安装并启动。
            </div>
          </div>
        </div>
        <button
          type="button"
          class="btn btn-xs btn-primary border-amber-500/60 bg-amber-600/30 text-amber-100 hover:bg-amber-600/50"
          :disabled="installing || loading"
          @click="installSambaOnHost()"
        >
          {{ installing ? '⏳ 正在安装...' : '⚡ 一键在宿主机安装 Samba' }}
        </button>
      </div>

      <!-- 头部控制栏 -->
      <div class="panel p-3.5 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-2.5">
          <span class="text-xl">📁</span>
          <div>
            <div class="flex items-center gap-2">
              <span class="font-display font-semibold text-slate-100 text-sm">Samba (SMB) 局域网文件共享</span>
              <span class="badge" :class="isRunning ? 'border-emerald-500/40 text-emerald-300' : 'border-slate-700 text-slate-500'">
                {{ isRunning ? 'smbd 运行中' : '未激活' }}
              </span>
              <span v-if="isHostPierced" class="badge border-cyan-500/40 text-cyan-300 text-[10px]">
                宿主穿透
              </span>
            </div>
            <div class="text-xs text-slate-400 mt-0.5">
              工作组: <span class="font-mono text-cyan-300/80">{{ workgroup }}</span> · 已配置 {{ shares.length }} 个共享 · {{ users.length }} 个凭据用户 · {{ sessions.length }} 个在线客户端
            </div>
          </div>
        </div>

        <div class="flex items-center gap-2">
          <!-- 标签页切换 -->
          <div class="flex rounded-md border border-cyber-line/50 p-0.5 bg-slate-900/60 text-xs mr-2">
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="activeTab === 'shares' ? 'bg-cyan-500/20 text-cyan-300 font-semibold' : 'text-slate-400 hover:text-slate-200'"
              @click="activeTab = 'shares'"
            >
              📂 共享目录 ({{ shares.length }})
            </button>
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="activeTab === 'users' ? 'bg-cyan-500/20 text-cyan-300 font-semibold' : 'text-slate-400 hover:text-slate-200'"
              @click="activeTab = 'users'"
            >
              👥 凭据用户 ({{ users.length }})
            </button>
            <button
              type="button"
              class="px-2.5 py-1 rounded transition-colors"
              :class="activeTab === 'sessions' ? 'bg-cyan-500/20 text-cyan-300 font-semibold' : 'text-slate-400 hover:text-slate-200'"
              @click="activeTab = 'sessions'"
            >
              📡 在线会话 ({{ sessions.length }})
            </button>
          </div>

          <button type="button" class="btn btn-xs" :disabled="loading || saving || installing" @click="loadConfig()">
            🔄 刷新
          </button>
          <button
            v-if="activeTab === 'shares'"
            type="button"
            class="btn btn-xs btn-primary"
            :disabled="loading || saving || installing"
            @click="openCreateShare()"
          >
            ➕ 新建共享
          </button>
          <button
            v-if="activeTab === 'users'"
            type="button"
            class="btn btn-xs btn-primary"
            :disabled="loading || saving || installing"
            @click="triggerAddUser()"
          >
            ➕ 新建用户
          </button>
        </div>
      </div>

      <!-- Tab 1: 共享列表 -->
      <div v-if="activeTab === 'shares'">
        <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
          正在读取 Samba 共享列表...
        </div>
        <div v-else-if="shares.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          当前暂无自定义 Samba 共享，点击「➕ 新建共享」创建首个共享目录。
        </div>
        <div v-else class="panel overflow-x-auto list-viewport">
          <table class="data-table min-w-[52rem]">
            <thead>
              <tr>
                <th>共享名称</th>
                <th>物理挂载路径 / 磁盘容量</th>
                <th>访问权限</th>
                <th>高级特性</th>
                <th>授权用户</th>
                <th>备注</th>
                <th class="text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="s in shares" :key="s.name">
                <td class="font-display font-semibold text-xs text-slate-200">
                  <div class="flex items-center gap-1.5">
                    <span>[{{ s.name }}]</span>
                    <span v-if="s.browseable === false" class="badge text-[10px] border-slate-700 text-slate-400" title="网络邻居隐藏共享">
                      🙈 隐藏
                    </span>
                  </div>
                </td>
                <td class="font-mono text-xs max-w-[20rem]">
                  <div class="truncate text-cyan-300/80" :title="s.path">{{ s.path }}</div>
                  <div class="flex items-center gap-1.5 mt-0.5">
                    <span v-if="!s.directoryExists" class="badge text-[10px] border-amber-500/40 text-amber-300">
                      ⚠️ 目录未创建
                    </span>
                    <span v-else-if="s.freeSpaceBytes" class="text-[10px] text-slate-400">
                      💾 {{ formatBytes(s.freeSpaceBytes) }} 可用 / {{ formatBytes(s.totalSpaceBytes) }}
                    </span>
                  </div>
                </td>
                <td>
                  <div class="flex flex-col gap-1 items-start">
                    <span class="badge text-[10px]" :class="s.readOnly ? 'border-slate-700 text-slate-400' : 'border-emerald-500/40 text-emerald-300'">
                      {{ s.readOnly ? '只读' : '可读写' }}
                    </span>
                    <span class="badge text-[10px]" :class="s.guestOk ? 'border-cyan-500/40 text-cyan-300' : 'border-slate-700 text-slate-500'">
                      {{ s.guestOk ? '允许访客' : '需认证' }}
                    </span>
                  </div>
                </td>
                <td>
                  <div class="flex flex-wrap gap-1">
                    <span v-if="s.enableRecycleBin" class="badge text-[10px] border-emerald-500/40 text-emerald-300" title="局域网防误删回收站开启">
                      ♻️ 回收站
                    </span>
                    <span v-if="s.enableAppleFruit" class="badge text-[10px] border-indigo-500/40 text-indigo-300" title="Apple / macOS 优化开启">
                      🍎 Mac优化
                    </span>
                    <span v-if="s.writeList" class="badge text-[10px] border-amber-500/30 text-amber-300" :title="'限定可写: ' + s.writeList">
                      ✍️ 写入白名单
                    </span>
                    <span v-if="!s.enableRecycleBin && !s.enableAppleFruit && !s.writeList" class="text-xs text-slate-500">-</span>
                  </div>
                </td>
                <td class="text-xs text-slate-300">
                  {{ s.validUsers || (s.guestOk ? '所有人 (免密)' : '所有授权用户') }}
                </td>
                <td class="text-xs text-slate-400 truncate max-w-[10rem]">
                  {{ s.comment || '-' }}
                </td>
                <td class="text-right whitespace-nowrap">
                  <button type="button" class="btn btn-xs mr-1" @click="openEditShare(s)">
                    ✏️ 编辑
                  </button>
                  <button type="button" class="btn btn-xs btn-danger" @click="removeShare(s.name)">
                    删除
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 2: Samba 用户管理组件 -->
      <div v-if="activeTab === 'users'">
        <SambaUsersPanel
          ref="usersPanelRef"
          :users="users"
          :loading="loading"
          @refresh="loadConfig"
        />
      </div>

      <!-- Tab 3: Samba 客户端在线会话与审计组件 -->
      <div v-if="activeTab === 'sessions'">
        <SambaSessionsPanel
          :sessions="sessions"
          :loading="loading"
          @refresh="loadConfig"
        />
      </div>

      <!-- 新建/编辑共享弹窗组件 -->
      <SambaShareModal
        :visible="showShareModal"
        :editingShare="editingShare"
        :users="users"
        :saving="saving"
        @close="showShareModal = false"
        @save="handleSaveShare"
      />
    </div>
  `,
});
