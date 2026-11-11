import { defineComponent, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'SambaUsersPanel',
  props: {
    users: {
      type: Array,
      default: () => [],
    },
    loading: {
      type: Boolean,
      default: false,
    },
  },
  emits: ['refresh'],
  setup(props, { emit }) {
    const saving = ref(false);
    const showUserModal = ref(false);
    const isEditingPassword = ref(false);
    const userForm = reactive({
      username: '',
      password: '',
      confirmPassword: '',
    });

    function openAddUserModal() {
      isEditingPassword.value = false;
      userForm.username = '';
      userForm.password = '';
      userForm.confirmPassword = '';
      showUserModal.value = true;
    }

    function openEditPasswordModal(username) {
      isEditingPassword.value = true;
      userForm.username = username;
      userForm.password = '';
      userForm.confirmPassword = '';
      showUserModal.value = true;
    }

    async function confirmSaveUser() {
      if (!userForm.username.trim()) {
        toast.warning('用户名不能为空');
        return;
      }
      if (!userForm.password) {
        toast.warning('密码不能为空');
        return;
      }
      if (userForm.password !== userForm.confirmPassword) {
        toast.warning('两次输入的密码不一致');
        return;
      }

      saving.value = true;
      try {
        if (isEditingPassword.value) {
          const res = await http(API.services.sambaUserPassword, {
            method: 'PUT',
            body: { username: userForm.username.trim(), password: userForm.password },
          });
          if (res.ok && res.data?.success) {
            toast.success(res.data.message || '密码修改成功');
            showUserModal.value = false;
            emit('refresh');
          } else {
            toast.error(res.data?.message || res.message || '修改失败');
          }
        } else {
          const res = await http(API.services.sambaUsers, {
            method: 'POST',
            body: { username: userForm.username.trim(), password: userForm.password },
          });
          if (res.ok && res.data?.success) {
            toast.success(res.data.message || 'Samba 访问用户创建成功');
            showUserModal.value = false;
            emit('refresh');
          } else {
            toast.error(res.data?.message || res.message || '创建用户失败');
          }
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    async function removeUser(username) {
      if (!confirm(`确定删除 Samba 访问用户 [${username}] 吗？此操作将注销其 SMB 访问权限。`)) return;
      saving.value = true;
      try {
        const res = await http(`${API.services.sambaUsers}/${encodeURIComponent(username)}`, {
          method: 'DELETE',
        });
        if (res.ok && res.data?.success) {
          toast.success(`用户 [${username}] 已删除`);
          emit('refresh');
        } else {
          toast.error(res.data?.message || res.message || '删除失败');
        }
      } catch (err) {
        toast.error('删除异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    return {
      saving,
      showUserModal,
      isEditingPassword,
      userForm,
      openAddUserModal,
      openEditPasswordModal,
      confirmSaveUser,
      removeUser,
    };
  },
  template: `
    <div>
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
        正在读取 Samba 凭据用户...
      </div>
      <div v-else-if="users.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        当前宿主机暂未配置 Samba 专属凭据用户，点击上方「➕ 新建用户」添加可用于客户端密码认证的账户。
      </div>
      <div v-else class="panel overflow-x-auto list-viewport">
        <table class="data-table min-w-[36rem]">
          <thead>
            <tr>
              <th>用户名 (Samba Account)</th>
              <th>凭据数据库状态</th>
              <th>备注信息</th>
              <th class="text-right">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="u in users" :key="u.username">
              <td class="font-mono font-semibold text-xs text-slate-200">
                {{ u.username }}
              </td>
              <td>
                <span class="badge text-[11px] border-emerald-500/40 text-emerald-300">
                  ✓ passdb 凭据就绪
                </span>
              </td>
              <td class="text-xs text-slate-400">
                {{ u.comment || 'Samba 专用访问账户' }}
              </td>
              <td class="text-right whitespace-nowrap space-x-1.5">
                <button type="button" class="btn btn-xs" @click="openEditPasswordModal(u.username)">
                  🔑 修改密码
                </button>
                <button type="button" class="btn btn-xs btn-danger" @click="removeUser(u.username)">
                  删除
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 用户新增/修改密码弹窗 -->
      <div v-if="showUserModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-md w-full flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">
              {{ isEditingPassword ? '修改 Samba 访问密码' : '新建 Samba 访问用户' }}
            </span>
            <button type="button" class="btn btn-xs" @click="showUserModal = false">✕ 关闭</button>
          </div>
          <div class="p-4 space-y-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">用户名 (Username)</label>
              <input
                type="text"
                v-model="userForm.username"
                :disabled="isEditingPassword"
                placeholder="如 smbuser, admin"
                class="input text-xs font-mono w-full"
              />
              <p v-if="!isEditingPassword" class="text-[11px] text-slate-500 mt-0.5">若宿主机不存在该 Linux 用户，将自动创建受限系统影子账户</p>
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">Samba 访问密码 (Password)</label>
              <input
                type="password"
                v-model="userForm.password"
                placeholder="输入访问密码"
                class="input text-xs font-mono w-full"
              />
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">确认密码 (Confirm Password)</label>
              <input
                type="password"
                v-model="userForm.confirmPassword"
                placeholder="再次输入密码"
                class="input text-xs font-mono w-full"
              />
            </div>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2">
            <button type="button" class="btn btn-xs" @click="showUserModal = false">取消</button>
            <button type="button" class="btn btn-xs btn-primary" :disabled="saving" @click="confirmSaveUser()">
              {{ isEditingPassword ? '确认修改' : '确认创建' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
