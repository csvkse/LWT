import { defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
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

const HEALTH_META = {
  Unknown: { label: '未检测', class: 'border-slate-600/60 text-slate-400', dot: 'bg-slate-500' },
  Healthy: { label: '可访问', class: 'border-emerald-500/50 text-emerald-300', dot: 'bg-emerald-400' },
  NotMounted: { label: '未挂载', class: 'border-slate-600/60 text-slate-400', dot: 'bg-slate-500' },
  ServerUnreachable: { label: '服务器不可达', class: 'border-amber-500/50 text-amber-300', dot: 'bg-amber-400' },
  Stale: { label: '挂载失效', class: 'border-rose-500/50 text-rose-300', dot: 'bg-rose-500' },
  Recovering: { label: '恢复中', class: 'border-cyan-500/50 text-cyan-300', dot: 'bg-cyan-400' },
  RecoveryFailed: { label: '恢复失败', class: 'border-rose-500/50 text-rose-300', dot: 'bg-rose-500' },
  Unsupported: { label: '不支持', class: 'border-amber-500/50 text-amber-300', dot: 'bg-amber-400' },
};

const HEALTH_ENUM = [
  'Unknown', 'Healthy', 'NotMounted', 'ServerUnreachable',
  'Stale', 'Recovering', 'RecoveryFailed', 'Unsupported',
];
const PHASES = ['空闲', '排队中', '探测中', '挂载中', '验证中', '卸载中', '等待重试', '等待处理'];
const MODES = ['已禁用', '仅监测', '自动维护', '手动暂停'];
const FAILURE_NAMES = ['None', 'Unreachable', 'AuthenticationFailed', 'Conflict', 'Busy', 'Timeout', 'Unsupported', 'Failed', 'Cancelled', 'PermissionDenied'];
const FAILURE_LABELS = { Unreachable: '远端不可达', AuthenticationFailed: '认证或权限失败', Conflict: '挂载身份冲突', Busy: '资源占用', Timeout: '操作超时', Unsupported: '依赖不可用', Failed: '操作失败', Cancelled: '任务取消', PermissionDenied: '目录权限或探测配置异常' };

const emptyForm = () => ({
  kind: 'smb', name: '', server: '', url: '', localPath: '', username: '', password: '', domain: '', options: 'vers=3.0,uid=1001,gid=1001',
  host: '', port: 22, remotePath: '', keyFile: '', hostKey: '', endpoint: '', bucket: '', region: '', accessKeyId: '', secretAccessKey: '',
  autoMount: false, enabled: true, description: '',
});

export default defineComponent({
  name: 'SmbMountsView',
  setup() {
    const router = useRouter();
    const items = ref([]);
    const loading = ref(false);
    const actingId = ref(null);
    const unsupported = ref(false);
    const webDavUnsupported = ref(false);
    const rcloneUnsupported = ref(false);
    const loadError = ref('');
    let disposed = false;
    const pendingTasks = new Map();

    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive(emptyForm());

    async function load() {
      if (loading.value || disposed) return;
      loading.value = true;
      loadError.value = '';
      try {
        const [supportResult, listResult, webDavSupport, webDavList, rcloneSupport, rcloneList] = await Promise.all([
          http(API.smbMounts.support),
          http(API.smbMounts.list, { method: 'GET' }),
          http(API.webDavMounts.support),
          http(API.webDavMounts.list, { method: 'GET' }),
          http(API.rcloneMounts.support),
          http(API.rcloneMounts.list, { method: 'GET' }),
        ]);
        if (supportResult.ok && supportResult.data && typeof supportResult.data.supported === 'boolean') {
          unsupported.value = !supportResult.data.supported;
        } else if (!supportResult.ok) {
          loadError.value = supportResult.message || '挂载能力检测失败';
        }
        webDavUnsupported.value = !webDavSupport.ok || !webDavSupport.data?.supported;
        rcloneUnsupported.value = !rcloneSupport.ok || !rcloneSupport.data?.supported;
        if (listResult.ok && Array.isArray(listResult.data) && webDavList.ok && Array.isArray(webDavList.data)
            && rcloneList.ok && Array.isArray(rcloneList.data)) {
          items.value = [
            ...listResult.data.map((item) => ({ ...item, kind: 'smb' })),
            ...webDavList.data.map((item) => ({ ...item, kind: 'webdav' })),
            ...rcloneList.data,
          ];
        } else if (!listResult.ok || !webDavList.ok || !rcloneList.ok) {
          loadError.value = loadError.value || listResult.message || webDavList.message || rcloneList.message || '挂载列表加载失败';
        } else {
          loadError.value = '挂载列表响应格式异常';
        }
      } catch (error) {
        loadError.value = error?.message || '挂载页初始化失败';
      } finally {
        loading.value = false;
      }
    }

    function statusMeta(status) {
      return STATUS_META[status] || STATUS_META[0];
    }

    function healthMeta(health) {
      const state = typeof health?.state === 'number'
        ? HEALTH_ENUM[health.state]
        : health?.state;
      return HEALTH_META[state] || HEALTH_META.Unknown;
    }

    function openCreate() {
      editingId.value = null;
      Object.assign(form, emptyForm());
      showEditor.value = true;
    }
    function phaseText(health) {
      return typeof health?.executionPhase === 'number' ? PHASES[health.executionPhase]
        : ({ Idle: '空闲', Queued: '排队中', Probing: '探测中', Mounting: '挂载中', Verifying: '验证中', Unmounting: '卸载中', WaitingRetry: '等待重试', WaitingAction: '等待处理' }[health?.executionPhase] || '');
    }
    function modeText(health) {
      return typeof health?.managementMode === 'number' ? MODES[health.managementMode]
        : ({ Disabled: '已禁用', MonitorOnly: '仅监测', Automatic: '自动维护', ManualPaused: '手动暂停' }[health?.managementMode] || '');
    }
    function failureText(health) {
      const name = typeof health?.failureKind === 'number' ? FAILURE_NAMES[health.failureKind] : health?.failureKind;
      return FAILURE_LABELS[name] || '';
    }
    function trackTask(result, mount) {
      if (!result.ok) return;
      if (result.data?.taskId) {
        pendingTasks.set(result.data.taskId, mount.name);
        toast.info('操作已排队，可查看挂载状态与执行进度');
        pollTasks();
      } else toast.success(result.data?.message || '操作完成');
    }
    let pollingTasks = false;
    async function pollTasks() {
      if (disposed || pollingTasks) return;
      pollingTasks = true;
      try {
        if (pendingTasks.size) await load();
        for (const [taskId, name] of pendingTasks) {
          const result = await http(API.mountTasks.item(taskId), { method: 'GET' });
          if (!result.ok) { pendingTasks.delete(taskId); continue; }
          if (result.data?.completed) {
            pendingTasks.delete(taskId);
            const message = `${name}：${result.data.message || '操作完成'}`;
            if (result.data.success) toast.success(message);
            else toast.error(message);
            await load();
          }
        }
      } finally { pollingTasks = false; }
    }

    function openEdit(mount) {
      editingId.value = mount.id;
      Object.assign(form, {
        name: mount.name,
        kind: mount.kind,
        server: mount.server || '',
        url: mount.url || '',
        localPath: mount.localPath,
        username: mount.username || '',
        password: '',
        domain: mount.domain || '',
        options: mount.options || 'vers=3.0,uid=1001,gid=1001',
        host: mount.host || '',
        port: mount.port || 22,
        remotePath: mount.remotePath || '',
        keyFile: mount.keyFile || '',
        hostKey: mount.hostKey || '',
        endpoint: mount.endpoint || '',
        bucket: mount.bucket || '',
        region: mount.region || '',
        accessKeyId: mount.accessKeyId || '',
        secretAccessKey: '',
        autoMount: mount.autoMount,
        enabled: mount.enabled,
        description: mount.description || '',
      });
      showEditor.value = true;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入名称');
      if (form.kind === 'smb' && !form.server.trim()) return toast.error('请输入服务器共享地址');
      if (form.kind === 'webdav' && !form.url.trim()) return toast.error('请输入 WebDAV HTTPS 地址');
      if (form.kind === 'sftp' && (!form.host.trim() || !form.hostKey.trim())) return toast.error('请输入 SFTP 主机和主机公钥');
      if (form.kind === 's3' && (!form.bucket.trim() || !form.accessKeyId.trim())) return toast.error('请输入 S3 存储桶和访问密钥');
      if (!form.localPath.trim()) return toast.error('请输入本地挂载点');
      saving.value = true;
      try {
        const shared = {
          name: form.name,
          localPath: form.localPath,
          username: form.username || null,
          password: form.password || null,
          autoMount: form.autoMount,
          enabled: form.enabled,
          description: form.description || null,
        };
        const payload = form.kind === 'smb'
          ? { ...shared, server: form.server, domain: form.domain || null, options: form.options || null }
          : form.kind === 'webdav' ? { ...shared, url: form.url }
            : { ...shared, kind: form.kind, host: form.host || null, port: Number(form.port),
              remotePath: form.remotePath || null, keyFile: form.keyFile || null, hostKey: form.hostKey || null,
              endpoint: form.endpoint || null, bucket: form.bucket || null, region: form.region || null,
              accessKeyId: form.accessKeyId || null, secretAccessKey: form.secretAccessKey || null };
        const endpoint = form.kind === 'smb' ? API.smbMounts
          : form.kind === 'webdav' ? API.webDavMounts : API.rcloneMounts;
        const result = editingId.value
          ? await http(endpoint.item(editingId.value), { method: 'PUT', body: payload })
          : await http(endpoint.list, { method: 'POST', body: payload });
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
        message: `确定删除「${mount.name}」（${mount.server || mount.url || mount.host || mount.bucket} → ${mount.localPath}）？挂载中会先自动卸载。${mount.kind !== 'smb' ? '请先确认待上传文件已同步；删除配置不会清除缓存。' : ''}`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const endpoint = mount.kind === 'smb' ? API.smbMounts : mount.kind === 'webdav' ? API.webDavMounts : API.rcloneMounts;
          const result = await http(endpoint.item(mount.id), { method: 'DELETE' });
          if (result.ok) {
            trackTask(result, mount);
            await load();
          }
        },
      });
    }

    async function mountNow(mount) {
      actingId.value = mount.id;
      try {
        const endpoint = mount.kind === 'smb' ? API.smbMounts : mount.kind === 'webdav' ? API.webDavMounts : API.rcloneMounts;
        const result = await http(endpoint.mount(mount.id), { method: 'POST' });
        trackTask(result, mount);
        await load();
      } finally {
        actingId.value = null;
      }
    }

    // 挂载成功后跳转到文件管理器，定位到该挂载点
    function browse(mount) {
      router.push({ path: '/files', query: { path: mount.localPath } });
    }

    function unmount(mount) {
      openConfirm({
        title: '卸载挂载',
        message: `确定卸载「${mount.name}」？${mount.kind !== 'smb' ? '将尝试正常卸载，请先确认待上传文件已同步。' : '占用中的文件访问会中断，可用懒卸载等待释放。'}`,
        confirmText: '卸载',
        danger: true,
        onConfirm: async () => {
          const endpoint = mount.kind === 'smb' ? API.smbMounts : mount.kind === 'webdav' ? API.webDavMounts : API.rcloneMounts;
          const result = await http(endpoint.unmount(mount.id), {
            method: 'POST',
            ...(mount.kind === 'smb' || mount.kind === 'webdav' ? { body: { lazy: mount.kind === 'smb' } } : {}),
          });
          trackTask(result, mount);
          await load();
        },
      });
    }

    // 挂载状态会随外部变化，打开页面期间每 10 秒刷新一次
    let timer = null;
    let taskTimer = null;
    onMounted(() => {
      load();
      timer = setInterval(load, 10000);
      taskTimer = setInterval(pollTasks, 2000);
    });
    onUnmounted(() => {
      disposed = true;
      if (timer) clearInterval(timer);
      if (taskTimer) clearInterval(taskTimer);
    });

    return {
      items, loading, actingId, unsupported, webDavUnsupported, rcloneUnsupported, loadError, showEditor, editingId, saving, form,
      load, openCreate, openEdit, save, remove, mountNow, browse, unmount, statusMeta, healthMeta, phaseText, modeText, failureText, formatTime,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="text-sm text-slate-400">磁盘挂载管理 <span class="text-slate-600">（SMB / WebDAV / SFTP / S3 · 需 Linux 挂载权限）</span></h2>
        <button class="btn btn-primary ml-auto" @click="openCreate()">＋ 新建挂载</button>
      </div>

      <div v-if="unsupported" class="panel !border-amber-500/40 bg-amber-500/5 text-amber-200/90 text-xs leading-relaxed px-4 py-3">
        当前系统不支持 SMB 挂载。桌面 Windows 部署仅可维护配置；Docker 部署请以
        <code class="font-mono text-amber-100">--privileged --user root</code> 运行。
      </div>
      <div v-if="webDavUnsupported" class="panel !border-amber-500/40 bg-amber-500/5 text-amber-200/90 text-xs leading-relaxed px-4 py-3">
        当前系统不支持 WebDAV 挂载：需要 Linux、rclone、/dev/fuse 和挂载权限。配置仍可保存。
      </div>
      <div v-if="rcloneUnsupported" class="panel !border-amber-500/40 bg-amber-500/5 text-amber-200/90 text-xs leading-relaxed px-4 py-3">
        当前系统不支持 SFTP/S3 挂载：需要 Linux、rclone、/dev/fuse 和挂载权限。配置仍可保存。
      </div>
      <div v-if="loadError" class="panel !border-rose-500/40 bg-rose-500/5 text-rose-200/90 text-xs px-4 py-3 flex items-center gap-3">
        <span>{{ loadError }}</span>
        <button class="btn btn-xs ml-auto" @click="load()">重试</button>
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[58rem]">
          <thead>
            <tr><th>状态</th><th>类型</th><th>名称</th><th>远端地址</th><th>本地挂载点</th><th>账户</th><th>选项</th><th>自挂</th><th class="text-right">操作</th></tr>
          </thead>
          <tbody>
            <tr v-if="!items.length && !loading"><td colspan="9" class="text-slate-600 py-8 text-center">暂无挂载配置，点右上角「新建挂载」</td></tr>
            <tr v-for="mount in items" :key="mount.kind + mount.id">
              <td>
                <span class="badge" :class="statusMeta(mount.status).class">
                  <span class="inline-block w-1.5 h-1.5 rounded-full mr-1.5 align-middle" :class="statusMeta(mount.status).dot"></span>{{ statusMeta(mount.status).label }}
                </span>
                <div class="mt-1">
                  <span class="badge" :class="healthMeta(mount.health).class">
                    <span class="inline-block w-1.5 h-1.5 rounded-full mr-1.5 align-middle" :class="healthMeta(mount.health).dot"></span>{{ healthMeta(mount.health).label }}
                  </span>
                  <div v-if="mount.health?.lastError" class="mt-1 max-w-[12rem] truncate text-rose-300/80 text-[11px]" :title="mount.health.lastError">
                    {{ mount.health.lastError }}
                  </div>
                  <div v-if="mount.health" class="mt-1 text-slate-400 text-[11px]">{{ modeText(mount.health) }} · {{ phaseText(mount.health) }}</div>
                  <div v-if="failureText(mount.health)" class="text-rose-300 text-[11px]">{{ failureText(mount.health) }}</div>
                  <div v-if="mount.health?.nextAttemptAt" class="text-amber-300 text-[11px]">下次尝试 {{ formatTime(mount.health.nextAttemptAt) }}</div>
                  <div v-if="mount.health?.lastCheckedAt" class="text-slate-500 text-[11px]">检测 {{ formatTime(mount.health.lastCheckedAt) }}</div>
                </div>
              </td>
              <td class="text-slate-400 text-xs">{{ mount.kind.toUpperCase() }}</td>
              <td class="text-slate-200">{{ mount.name }}</td>
              <td class="font-mono text-xs text-cyan-300/80">{{ mount.server || mount.url || (mount.kind === 'sftp' ? mount.host + ':' + mount.remotePath : mount.bucket + (mount.remotePath ? '/' + mount.remotePath : '')) }}</td>
              <td class="font-mono text-xs text-slate-400">{{ mount.localPath }}</td>
              <td class="text-slate-500 text-xs">
                {{ mount.username || mount.accessKeyId || '访客' }}
                <span v-if="mount.hasPassword || mount.hasSecretAccessKey" class="text-slate-600"> · <span class="text-emerald-500/70">密钥已存</span></span>
              </td>
              <td class="max-w-[10rem] truncate text-slate-600 text-xs" :title="mount.options">{{ mount.kind === 'smb' ? (mount.options || '—') : '写缓存' }}</td>
              <td class="text-slate-500 text-xs">{{ mount.autoMount ? '是' : '否' }}</td>
              <td class="text-right whitespace-nowrap">
                <template v-if="mount.status !== 3">
                  <button v-if="mount.status !== 1" class="btn btn-xs btn-primary" :disabled="actingId === mount.id" @click="mountNow(mount)">
                    {{ actingId === mount.id ? '挂载中…' : '⏏ 挂载' }}
                  </button>
                  <template v-else>
                    <button class="btn btn-xs btn-primary" @click="browse(mount)">📂 浏览</button>
                    <button class="btn btn-xs btn-danger" @click="unmount(mount)">⏐ 卸载</button>
                  </template>
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
              <span class="text-xs text-slate-500 mb-1 block">协议 *</span>
              <select class="input" v-model="form.kind" :disabled="!!editingId">
                <option value="smb">SMB</option><option value="webdav">WebDAV</option>
                <option value="sftp">SFTP</option><option value="s3">S3</option>
              </select>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">名称 *</span>
              <input class="input" v-model="form.name" placeholder="例如：NAS 影视盘" />
            </label>
            <div v-if="form.kind === 'smb' || form.kind === 'webdav'" class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">{{ form.kind === 'smb' ? '共享地址' : 'WebDAV HTTPS 地址' }} *</span>
                <input v-if="form.kind === 'smb'" class="input font-mono" v-model="form.server" placeholder="//192.168.1.10/media" />
                <input v-else class="input font-mono" v-model="form.url" placeholder="https://example.com/remote.php/dav/files/user/" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">本地挂载点 *</span>
                <input class="input font-mono" v-model="form.localPath" placeholder="/mnt/media" />
              </label>
            </div>
            <div v-else class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">本地挂载点 *</span>
                <input class="input font-mono" v-model="form.localPath" placeholder="/mnt/remote" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">{{ form.kind === 'sftp' ? '远端目录' : '桶内前缀' }}</span>
                <input class="input font-mono" v-model="form.remotePath" :placeholder="form.kind === 'sftp' ? '/data' : 'optional/prefix'" />
              </label>
            </div>
            <div v-if="form.kind === 'sftp'" class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">SFTP 主机 *</span><input class="input" v-model="form.host" placeholder="sftp.example.com" /></label>
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">端口 *</span><input class="input" type="number" min="1" max="65535" v-model.number="form.port" /></label>
              <label class="block sm:col-span-2"><span class="text-xs text-slate-500 mb-1 block">SSH 主机公钥 *（算法 + Base64 公钥）</span><input class="input font-mono" v-model="form.hostKey" placeholder="ssh-ed25519 AAAAC3..." /></label>
              <label class="block sm:col-span-2"><span class="text-xs text-slate-500 mb-1 block">容器内私钥文件路径（可代替密码）</span><input class="input font-mono" v-model="form.keyFile" placeholder="/app/data/ssh/id_ed25519" /></label>
            </div>
            <div v-if="form.kind === 's3'" class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">存储桶 *</span><input class="input font-mono" v-model="form.bucket" placeholder="my-bucket" /></label>
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">区域（AWS 必填）</span><input class="input font-mono" v-model="form.region" placeholder="us-east-1" /></label>
              <label class="block sm:col-span-2"><span class="text-xs text-slate-500 mb-1 block">自定义 HTTPS 端点（兼容 S3 时填写）</span><input class="input font-mono" v-model="form.endpoint" placeholder="https://s3.example.com" /></label>
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">Access Key ID *</span><input class="input font-mono" v-model="form.accessKeyId" autocomplete="off" /></label>
              <label class="block"><span class="text-xs text-slate-500 mb-1 block">Secret Access Key *</span><input class="input" type="password" v-model="form.secretAccessKey" :placeholder="editingId ? '留空=保持不变' : ''" autocomplete="new-password" /></label>
            </div>
            <div v-if="form.kind === 'smb' || form.kind === 'webdav' || form.kind === 'sftp'" class="grid grid-cols-3 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">用户名{{ form.kind === 'sftp' ? ' *' : '' }}</span>
                <input class="input" v-model="form.username" :placeholder="form.kind === 'sftp' ? 'SFTP 用户' : '留空=访客'" />
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">密码</span>
                <input class="input" type="password" v-model="form.password" :placeholder="editingId ? '留空=保持不变' : ''" autocomplete="new-password" />
              </label>
              <label v-if="form.kind === 'smb'" class="block">
                <span class="text-xs text-slate-500 mb-1 block">域</span>
                <input class="input" v-model="form.domain" placeholder="可选" />
              </label>
            </div>
            <label v-if="form.kind === 'smb'" class="block">
              <span class="text-xs text-slate-500 mb-1 block">附加挂载选项（逗号分隔）</span>
              <input class="input font-mono" v-model="form.options" placeholder="vers=3.0,uid=1001,gid=1001" />
              <p class="text-[11px] text-slate-600 mt-1 leading-relaxed">
                常见：<code class="font-mono">vers=3.0</code> 强制 SMB3；容器内建议
                <code class="font-mono">uid=1001,gid=1001</code> 使文件对应用户可读写；<code class="font-mono">ro</code> 只读。
                密码不会出现在命令行，自动写入 data/mount-creds 凭据文件（600 权限）。
              </p>
            </label>
            <p v-if="form.kind === 'webdav'" class="text-[11px] text-slate-500 leading-relaxed">
              WebDAV 使用 rclone FUSE 挂载与写缓存；请使用 HTTPS 和应用密码。缓存中的待上传文件在卸载后仍会保留。
            </p>
            <p v-if="form.kind === 'sftp'" class="text-[11px] text-slate-500 leading-relaxed">
              SFTP 必须校验服务器主机公钥。请从可信渠道取得完整公钥；私钥文件需预先放进容器可访问的路径。
            </p>
            <p v-if="form.kind === 's3'" class="text-[11px] text-slate-500 leading-relaxed">
              S3 挂载使用 rclone FUSE 写缓存。自定义兼容服务仅接受 HTTPS 端点；卸载前请确认待上传文件已同步。
            </p>
            <div class="flex gap-5">
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.enabled" class="accent-cyan-400" /> 启用配置
              </label>
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.autoMount" class="accent-cyan-400" /> 自动挂载与故障恢复
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
