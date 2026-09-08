import { defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { formatTime } from '../utils/format.js';

const STATUS_META = {
  0: { label: '未挂载', class: 'border-slate-600/60 text-slate-400', dot: 'bg-slate-500' },
  1: { label: '已挂载', class: 'border-emerald-500/50 text-emerald-300', dot: 'bg-emerald-400' },
  2: { label: '异常占用', class: 'border-rose-500/50 text-rose-300', dot: 'bg-rose-500' },
  3: { label: '不支持', class: 'border-amber-500/50 text-amber-300', dot: 'bg-amber-400' },
};

const emptyForm = () => ({
  name: '', server: '', localPath: '', username: '', password: '', domain: '', options: 'vers=3.0,uid=1001,gid=1001',
  autoMount: false, enabled: true, description: '',
});

export default defineComponent({
  name: 'SmbMountsView',
  setup() {
    const items = ref([]);
    const loading = ref(false);
    const actingId = ref(null);
    const unsupported = ref(false);

    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive(emptyForm());

    async function load() {
      loading.value = true;
      try {
        const [supportResult, listResult] = await Promise.all([
          http(API.smbMounts.support),
          http(API.smbMounts.list),
        ]);
        if (supportResult.ok) unsupported.value = !supportResult.data.supported;
        if (listResult.ok) items.value = listResult.data;
      } finally {
        loading.value = false;
      }
    }

    function statusMeta(status) {
      return STATUS_META[status] || STATUS_META[0];
    }

    function openCreate() {
      editingId.value = null;
      Object.assign(form, emptyForm());
      showEditor.value = true;
    }

    function openEdit(mount) {
      editingId.value = mount.id;
      Object.assign(form, {
        name: mount.name,
        server: mount.server,
        localPath: mount.localPath,
        username: mount.username || '',
        password: '',
        domain: mount.domain || '',
        options: mount.options || 'vers=3.0,uid=1001,gid=1001',
        autoMount: mount.autoMount,
        enabled: mount.enabled,
        description: mount.description || '',
      });
      showEditor.value = true;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入名称');
      if (!form.server.trim()) return toast.error('请输入服务器共享地址');
      if (!form.localPath.trim()) return toast.error('请输入本地挂载点');
      saving.value = true;
      try {
        const payload = {
          name: form.name,
          server: form.server,
          localPath: form.localPath,
          username: form.username || null,
          password: form.password || null,
          domain: form.domain || null,
          options: form.options || null,
          autoMount: form.autoMount,
          enabled: form.enabled,
          description: form.description || null,
        };
        const result = editingId.value
          ? await http(API.smbMounts.item(editingId.value), { method: 'PUT', body: payload })
          : await http(API.smbMounts.list, { method: 'POST', body: payload });
        if (result.ok) {
          toast.success(editingId.value ? '配置已保存' : '配置已创建');
          showEditor.value = false;
          await load();
        }
      } finally {
        saving.value = false;
      }
    }

    function remove(mount) {
      openConfirm({
        title: '删除挂载配置',
        message: `确定删除「${mount.name}」（${mount.server} → ${mount.localPath}）？挂载中会先自动卸载。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.smbMounts.item(mount.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('配置已删除');
            await load();
          }
        },
      });
    }

    async function mountNow(mount) {
      actingId.value = mount.id;
      try {
        const result = await http(API.smbMounts.mount(mount.id), { method: 'POST' });
        if (result.ok) toast.success(result.data.message || '挂载成功');
        await load();
      } finally {
        actingId.value = null;
      }
    }

    function unmount(mount) {
      openConfirm({
        title: '卸载挂载',
        message: `确定卸载「${mount.name}」？占用中的文件访问会中断，可用懒卸载等待释放。`,
        confirmText: '卸载',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.smbMounts.unmount(mount.id), {
            method: 'POST',
            body: { lazy: true },
          });
          if (result.ok) toast.success(result.data.message || '已卸载');
          await load();
        },
      });
    }

    // 挂载状态会随外部变化，打开页面期间每 10 秒刷新一次
    let timer = null;
    onMounted(() => {
      load();
      timer = setInterval(load, 10000);
    });
    onUnmounted(() => {
      if (timer) clearInterval(timer);
    });

    return {
      items, loading, actingId, unsupported, showEditor, editingId, saving, form,
      load, openCreate, openEdit, save, remove, mountNow, unmount, statusMeta, formatTime,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="text-sm text-slate-400">SMB 挂载管理 <span class="text-slate-600">（mount -t cifs · 需 Linux 特权环境）</span></h2>
        <button class="btn btn-primary ml-auto" @click="openCreate()">＋ 新建挂载</button>
      </div>

      <div v-if="unsupported" class="panel !border-amber-500/40 bg-amber-500/5 text-amber-200/90 text-xs leading-relaxed px-4 py-3">
        当前系统不支持挂载管理（需 Linux 环境）。桌面 Windows 部署仅可维护配置；Docker 部署请以
        <code class="font-mono text-amber-100">--privileged --user root</code> 运行（镜像含 cifs-utils）。
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[58rem]">
          <thead>
            <tr><th>状态</th><th>名称</th><th>共享地址</th><th>本地挂载点</th><th>账户</th><th>选项</th><th>自挂</th><th class="text-right">操作</th></tr>
          </thead>
          <tbody>
            <tr v-if="!items.length && !loading"><td colspan="8" class="text-slate-600 py-8 text-center">暂无挂载配置，点右上角「新建挂载」</td></tr>
            <tr v-for="mount in items" :key="mount.id">
              <td>
                <span class="badge" :class="statusMeta(mount.status).class">
                  <span class="inline-block w-1.5 h-1.5 rounded-full mr-1.5 align-middle" :class="statusMeta(mount.status).dot"></span>{{ statusMeta(mount.status).label }}
                </span>
              </td>
              <td class="text-slate-200">{{ mount.name }}</td>
              <td class="font-mono text-xs text-cyan-300/80">{{ mount.server }}</td>
              <td class="font-mono text-xs text-slate-400">{{ mount.localPath }}</td>
              <td class="text-slate-500 text-xs">
                {{ mount.username || '访客' }}
                <span v-if="mount.hasPassword" class="text-slate-600"> · <span class="text-emerald-500/70">密码已存</span></span>
              </td>
              <td class="max-w-[10rem] truncate text-slate-600 text-xs" :title="mount.options">{{ mount.options || '—' }}</td>
              <td class="text-slate-500 text-xs">{{ mount.autoMount ? '是' : '否' }}</td>
              <td class="text-right whitespace-nowrap">
                <template v-if="mount.status !== 3">
                  <button v-if="mount.status !== 1" class="btn btn-xs btn-primary" :disabled="actingId === mount.id" @click="mountNow(mount)">
                    {{ actingId === mount.id ? '挂载中…' : '⏏ 挂载' }}
                  </button>
                  <button v-else class="btn btn-xs btn-danger" @click="unmount(mount)">⏐ 卸载</button>
                </template>
                <button class="btn btn-xs" @click="openEdit(mount)">编辑</button>
                <button class="btn btn-xs btn-danger" @click="remove(mount)">删除</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="showEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 max-h-[90vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingId ? '编辑挂载' : '新建挂载' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">名称 *</span>
              <input class="input" v-model="form.name" placeholder="例如：NAS 影视盘" />
            </label>
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">共享地址 *</span>
                <input class="input font-mono" v-model="form.server" placeholder="//192.168.1.10/media" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">本地挂载点 *</span>
                <input class="input font-mono" v-model="form.localPath" placeholder="/mnt/media" />
              </label>
            </div>
            <div class="grid grid-cols-3 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">用户名</span>
                <input class="input" v-model="form.username" placeholder="留空=访客" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">密码</span>
                <input class="input" type="password" v-model="form.password" :placeholder="editingId ? '留空=保持不变' : ''" autocomplete="new-password" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">域</span>
                <input class="input" v-model="form.domain" placeholder="可选" />
              </label>
            </div>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">附加挂载选项（逗号分隔）</span>
              <input class="input font-mono" v-model="form.options" placeholder="vers=3.0,uid=1001,gid=1001" />
              <p class="text-[11px] text-slate-600 mt-1 leading-relaxed">
                常见：<code class="font-mono">vers=3.0</code> 强制 SMB3；容器内建议
                <code class="font-mono">uid=1001,gid=1001</code> 使文件对应用户可读写；<code class="font-mono">ro</code> 只读。
                密码不会出现在命令行，自动写入 data/mount-creds 凭据文件（600 权限）。
              </p>
            </label>
            <div class="flex gap-5">
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.enabled" class="accent-cyan-400" /> 启用配置
              </label>
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.autoMount" class="accent-cyan-400" /> 应用启动时自动挂载
              </label>
            </div>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">备注</span>
              <input class="input" v-model="form.description" placeholder="可选" />
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="save()">{{ saving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
