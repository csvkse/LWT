import { defineComponent, reactive, ref, watch } from 'vue';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'SambaShareModal',
  props: {
    visible: { type: Boolean, default: false },
    editingShare: { type: Object, default: null },
    users: { type: Array, default: () => [] },
    saving: { type: Boolean, default: false },
  },
  emits: ['close', 'save'],
  setup(props, { emit }) {
    const showAdvanced = ref(false);
    const form = reactive({
      name: '',
      path: '/var/docker/shares',
      readOnly: false,
      guestOk: true,
      browseable: true,
      selectedUsers: [],
      customUsers: '',
      writeList: '',
      readList: '',
      enableRecycleBin: false,
      enableAppleFruit: false,
      forceUser: '',
      comment: '',
    });

    watch(() => props.visible, (val) => {
      if (!val) return;
      showAdvanced.value = false;
      const s = props.editingShare;
      if (s) {
        form.name = s.name || '';
        form.path = s.path || '';
        form.readOnly = Boolean(s.readOnly);
        form.guestOk = Boolean(s.guestOk);
        form.browseable = s.browseable !== false;
        form.enableRecycleBin = Boolean(s.enableRecycleBin);
        form.enableAppleFruit = Boolean(s.enableAppleFruit);
        form.forceUser = s.forceUser || '';
        form.writeList = s.writeList || '';
        form.readList = s.readList || '';
        form.comment = s.comment || '';

        // 分离已知的凭据用户与未知自定义用户
        const rawUsers = (s.validUsers || '').split(/[\s,]+/).filter(Boolean);
        const knownUsers = (props.users || []).map(u => u.username);
        form.selectedUsers = rawUsers.filter(u => knownUsers.includes(u));
        form.customUsers = rawUsers.filter(u => !knownUsers.includes(u)).join(' ');
      } else {
        form.name = '';
        form.path = '/var/docker/shares';
        form.readOnly = false;
        form.guestOk = true;
        form.browseable = true;
        form.selectedUsers = [];
        form.customUsers = '';
        form.writeList = '';
        form.readList = '';
        form.enableRecycleBin = false;
        form.enableAppleFruit = false;
        form.forceUser = '';
        form.comment = '';
      }
    });

    function toggleUser(username) {
      const idx = form.selectedUsers.indexOf(username);
      if (idx >= 0) form.selectedUsers.splice(idx, 1);
      else form.selectedUsers.push(username);
    }

    function confirmSave() {
      if (!form.name.trim() || !form.path.trim()) {
        toast.warning('共享名与物理路径不能为空');
        return;
      }

      // 前端高危路径快速阻断
      const p = form.path.trim().replace(/\\/g, '/');
      if (p === '/' || /^\/(etc|boot|sys|proc|dev|bin|sbin|lib|lib64|root)(\/|$)/i.test(p)) {
        toast.error('禁止将系统核心根目录设为局域网共享');
        return;
      }

      const allUsers = [...form.selectedUsers];
      if (form.customUsers?.trim()) {
        const customParts = form.customUsers.split(/[\s,]+/).filter(Boolean);
        for (const u of customParts) {
          if (!allUsers.includes(u)) allUsers.push(u);
        }
      }

      emit('save', {
        name: form.name.trim(),
        path: form.path.trim(),
        readOnly: Boolean(form.readOnly),
        guestOk: Boolean(form.guestOk),
        browseable: Boolean(form.browseable),
        validUsers: allUsers.length > 0 ? allUsers.join(' ') : null,
        writeList: form.writeList?.trim() || null,
        readList: form.readList?.trim() || null,
        enableRecycleBin: Boolean(form.enableRecycleBin),
        enableAppleFruit: Boolean(form.enableAppleFruit),
        forceUser: form.forceUser?.trim() || null,
        comment: form.comment?.trim() || null,
        isEdit: Boolean(props.editingShare),
        originalName: props.editingShare?.name || form.name.trim(),
      });
    }

    return { form, showAdvanced, toggleUser, confirmSave };
  },
  template: `
    <div v-if="visible" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
      <div class="panel max-w-lg w-full flex flex-col border-cyan-500/40 shadow-2xl max-h-[90vh] overflow-y-auto">
        <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between sticky top-0 bg-slate-900/90 backdrop-blur z-10">
          <span class="font-display font-semibold text-slate-100 text-sm">
            {{ editingShare ? '✏️ 编辑 Samba 共享 [' + editingShare.name + ']' : '➕ 新建 Samba 共享文件夹' }}
          </span>
          <button type="button" class="btn btn-xs" @click="$emit('close')">✕ 关闭</button>
        </div>
        <div class="p-4 space-y-3">
          <div>
            <label class="block text-xs text-slate-400 mb-1">共享名称 (Section Name)</label>
            <input type="text" v-model="form.name" :disabled="Boolean(editingShare)" placeholder="如 public, movies, backup" class="input text-xs w-full disabled:opacity-60" />
            <p v-if="editingShare" class="text-[11px] text-slate-500 mt-0.5">编辑模式下共享名称不可修改</p>
          </div>
          <div>
            <label class="block text-xs text-slate-400 mb-1">物理宿主绝对路径 (Path)</label>
            <input type="text" v-model="form.path" placeholder="/var/docker/shares" class="input text-xs font-mono w-full" />
            <p class="text-[11px] text-slate-500 mt-0.5">若宿主机目录不存在，保存时将自动创建并配置 0775 读写权限</p>
          </div>
          <div class="flex items-center justify-between pt-1">
            <div>
              <div class="text-xs font-medium text-slate-200">允许访客匿名访问 (guest ok)</div>
              <div class="text-[11px] text-slate-500">局域网内任意客户端无需密码即可直连</div>
            </div>
            <input type="checkbox" v-model="form.guestOk" class="toggle" />
          </div>
          <div class="flex items-center justify-between pt-1">
            <div>
              <div class="text-xs font-medium text-slate-200">只读限制 (read only)</div>
              <div class="text-[11px] text-slate-500">开启后客户端仅能读取，禁止创建、修改与删除</div>
            </div>
            <input type="checkbox" v-model="form.readOnly" class="toggle" />
          </div>

          <!-- 授权用户选择 -->
          <div v-if="!form.guestOk" class="pt-2 border-t border-cyber-line/30">
            <label class="block text-xs text-slate-300 mb-1.5 font-medium">指定授权访问用户 (valid users)</label>
            <div v-if="users.length > 0" class="flex flex-wrap gap-2 mb-2">
              <button
                type="button"
                v-for="u in users"
                :key="u.username"
                class="badge cursor-pointer transition-colors"
                :class="form.selectedUsers.includes(u.username) ? 'border-cyan-400 bg-cyan-500/20 text-cyan-200' : 'border-slate-700 bg-slate-900/40 text-slate-400 hover:border-slate-500'"
                @click="toggleUser(u.username)"
              >
                {{ form.selectedUsers.includes(u.username) ? '✓ ' : '+ ' }}{{ u.username }}
              </button>
            </div>
            <div v-else class="text-[11px] text-amber-300/80 mb-2">
              ⚠️ 当前尚未配置 Samba 凭据用户，请先在「凭据用户」Tab 中添加用户，或开启「允许访客匿名访问」。
            </div>
            <input
              type="text"
              v-model="form.customUsers"
              placeholder="手动补充其他用户或组（如 user1, @smbgroup）"
              class="input text-xs font-mono w-full"
            />
          </div>

          <!-- 高级共享参数折叠栏 -->
          <div class="pt-2 border-t border-cyber-line/30">
            <button
              type="button"
              class="flex items-center justify-between w-full text-xs text-slate-400 hover:text-slate-200 py-1"
              @click="showAdvanced = !showAdvanced"
            >
              <span class="font-medium">⚙️ 高级选项（回收站、隐藏共享与权限白名单）</span>
              <span class="text-xs font-mono">{{ showAdvanced ? '▲ 收起' : '▼ 展开' }}</span>
            </button>

            <div v-if="showAdvanced" class="space-y-3 pt-2.5">
              <div class="flex items-center justify-between">
                <div>
                  <div class="text-xs font-medium text-slate-200">网络邻居可见 (browseable)</div>
                  <div class="text-[11px] text-slate-500">关闭后将作为隐藏共享，需手动输入路径连接</div>
                </div>
                <input type="checkbox" v-model="form.browseable" class="toggle" />
              </div>

              <div class="flex items-center justify-between">
                <div>
                  <div class="text-xs font-medium text-slate-200">♻️ 局域网防误删回收站 (VFS Recycle)</div>
                  <div class="text-[11px] text-slate-500">删除文件自动安全移动至 .recycle 隔离目录</div>
                </div>
                <input type="checkbox" v-model="form.enableRecycleBin" class="toggle" />
              </div>

              <div class="flex items-center justify-between">
                <div>
                  <div class="text-xs font-medium text-slate-200">🍎 macOS / Apple 优化 (VFS Fruit)</div>
                  <div class="text-[11px] text-slate-500">消除 .DS_Store 冲突并加速 Mac 局域网访问</div>
                </div>
                <input type="checkbox" v-model="form.enableAppleFruit" class="toggle" />
              </div>

              <div>
                <label class="block text-xs text-slate-400 mb-1">可写用户白名单 (write list)</label>
                <input type="text" v-model="form.writeList" placeholder="如 admin, @managers (留空继承只读设置)" class="input text-xs font-mono w-full" />
              </div>

              <div>
                <label class="block text-xs text-slate-400 mb-1">只读用户白名单 (read list)</label>
                <input type="text" v-model="form.readList" placeholder="如 guest, readonly_user (可选)" class="input text-xs font-mono w-full" />
              </div>

              <div>
                <label class="block text-xs text-slate-400 mb-1">强制归属用户 (force user)</label>
                <input type="text" v-model="form.forceUser" placeholder="统一文件创建属主（如 root 或 nobody）" class="input text-xs font-mono w-full" />
              </div>
            </div>
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">描述与备注 (Comment)</label>
            <input type="text" v-model="form.comment" placeholder="可选备注说明" class="input text-xs w-full" />
          </div>
        </div>
        <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2 sticky bottom-0 bg-slate-900/90 backdrop-blur">
          <button type="button" class="btn btn-xs" @click="$emit('close')">取消</button>
          <button type="button" class="btn btn-xs btn-primary" :disabled="saving" @click="confirmSave()">
            {{ saving ? '保存中...' : (editingShare ? '保存修改' : '保存并重载') }}
          </button>
        </div>
      </div>
    </div>
  `,
});
