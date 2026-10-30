import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'SambaSubmodule',
  setup() {
    const loading = ref(false);
    const saving = ref(false);
    const isRunning = ref(false);
    const workgroup = ref('WORKGROUP');
    const shares = ref([]);

    // 新建或编辑弹窗
    const showModal = ref(false);
    const form = reactive({
      name: '',
      path: '',
      readOnly: false,
      guestOk: true,
      validUsers: '',
      comment: '',
    });

    async function loadConfig() {
      loading.value = true;
      try {
        const res = await http(API.services.sambaConfig, { method: 'GET' });
        if (res.ok && res.data) {
          isRunning.value = res.data.isRunning;
          workgroup.value = res.data.workgroup || 'WORKGROUP';
          shares.value = res.data.shares || [];
        } else {
          toast.error(res.message || '加载 Samba 配置失败');
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function saveShares() {
      saving.value = true;
      try {
        const res = await http(API.services.sambaConfig, {
          method: 'POST',
          body: shares.value,
        });
        if (res.ok && res.data?.success) {
          toast.success('Samba 共享配置保存成功并已重载');
          await loadConfig();
        } else {
          toast.error(res.data?.message || res.message || '保存失败');
        }
      } catch (err) {
        toast.error('保存失败: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    function openAddModal() {
      form.name = '';
      form.path = '/data/shares';
      form.readOnly = false;
      form.guestOk = true;
      form.validUsers = '';
      form.comment = '';
      showModal.value = true;
    }

    function confirmAdd() {
      if (!form.name.trim() || !form.path.trim()) {
        toast.warning('共享名与路径不能为空');
        return;
      }
      if (shares.value.some(s => s.name.toLowerCase() === form.name.trim().toLowerCase())) {
        toast.warning('已存在同名共享块');
        return;
      }
      shares.value.push({
        name: form.name.trim(),
        path: form.path.trim(),
        readOnly: Boolean(form.readOnly),
        guestOk: Boolean(form.guestOk),
        validUsers: form.validUsers?.trim() || null,
        comment: form.comment?.trim() || null,
      });
      showModal.value = false;
      saveShares();
    }

    function removeShare(index) {
      shares.value.splice(index, 1);
      saveShares();
    }

    onMounted(() => {
      loadConfig();
    });

    return {
      loading,
      saving,
      isRunning,
      workgroup,
      shares,
      showModal,
      form,
      loadConfig,
      saveShares,
      openAddModal,
      confirmAdd,
      removeShare,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
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
            </div>
            <div class="text-xs text-slate-400 mt-0.5">
              工作组: <span class="font-mono text-cyan-300/80">{{ workgroup }}</span> · 已配置 {{ shares.length }} 个共享文件夹
            </div>
          </div>
        </div>
        <div class="flex items-center gap-2">
          <button type="button" class="btn btn-xs" :disabled="loading || saving" @click="loadConfig()">
            🔄 刷新
          </button>
          <button type="button" class="btn btn-xs btn-primary" :disabled="loading || saving" @click="openAddModal()">
            ➕ 新建共享
          </button>
        </div>
      </div>

      <!-- 共享列表 -->
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
        正在读取 Samba 共享列表...
      </div>
      <div v-else-if="shares.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        当前暂无自定义 Samba 共享，点击「➕ 新建共享」创建首个共享目录。
      </div>
      <div v-else class="panel overflow-x-auto">
        <table class="data-table min-w-[48rem]">
          <thead>
            <tr>
              <th>共享名称</th>
              <th>物理挂载路径</th>
              <th>访问权限</th>
              <th>访客访问</th>
              <th>授权用户</th>
              <th class="text-right">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(s, idx) in shares" :key="s.name">
              <td class="font-display font-semibold text-xs text-slate-200">
                [{{ s.name }}]
              </td>
              <td class="font-mono text-xs text-cyan-300/80 truncate max-w-[18rem]" :title="s.path">
                {{ s.path }}
              </td>
              <td>
                <span class="badge text-[11px]" :class="s.readOnly ? 'border-slate-700 text-slate-400' : 'border-emerald-500/40 text-emerald-300'">
                  {{ s.readOnly ? '只读' : '可读写' }}
                </span>
              </td>
              <td>
                <span class="badge text-[11px]" :class="s.guestOk ? 'border-cyan-500/40 text-cyan-300' : 'border-slate-700 text-slate-500'">
                  {{ s.guestOk ? '允许免密访客' : '需认证' }}
                </span>
              </td>
              <td class="text-xs text-slate-400">
                {{ s.validUsers || '所有授权用户' }}
              </td>
              <td class="text-right whitespace-nowrap">
                <button type="button" class="btn btn-xs btn-danger" @click="removeShare(idx)">
                  删除
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 新建共享弹窗 -->
      <div v-if="showModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-lg w-full flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">新建 Samba 共享文件夹</span>
            <button type="button" class="btn btn-xs" @click="showModal = false">✕ 关闭</button>
          </div>
          <div class="p-4 space-y-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">共享名称 (Section Name)</label>
              <input type="text" v-model="form.name" placeholder="如 public, movies, backup" class="input text-xs w-full" />
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">物理绝对路径 (Path)</label>
              <input type="text" v-model="form.path" placeholder="/data/shares" class="input text-xs font-mono w-full" />
            </div>
            <div class="flex items-center justify-between pt-1">
              <div>
                <div class="text-xs font-medium text-slate-200">允许访客匿名访问 (guest ok)</div>
                <div class="text-[11px] text-slate-500">局域网内任意客户端无需密码即可挂载</div>
              </div>
              <input type="checkbox" v-model="form.guestOk" class="toggle" />
            </div>
            <div class="flex items-center justify-between pt-1">
              <div>
                <div class="text-xs font-medium text-slate-200">只读限制 (read only)</div>
                <div class="text-[11px] text-slate-500">开启后客户端仅能读取，禁止修改删除</div>
              </div>
              <input type="checkbox" v-model="form.readOnly" class="toggle" />
            </div>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2">
            <button type="button" class="btn btn-xs" @click="showModal = false">取消</button>
            <button type="button" class="btn btn-xs btn-primary" @click="confirmAdd()">保存共享</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
