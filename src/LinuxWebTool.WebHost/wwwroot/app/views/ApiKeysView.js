import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { formatTime } from '../utils/format.js';

export default defineComponent({
  name: 'ApiKeysView',
  setup() {
    const keys = ref([]);
    const loading = ref(false);

    // 新建 / 编辑弹窗
    const showModal = ref(false);
    const isEdit = ref(false);
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

    async function load() {
      loading.value = true;
      const res = await http(API.apiKeys.list, { method: 'GET' });
      loading.value = false;
      if (res.ok) {
        keys.value = res.data || [];
      }
    }

    function openCreate() {
      isEdit.value = false;
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
        expiresAt: '',
      });
      showModal.value = true;
    }

    function openEdit(key) {
      isEdit.value = true;
      currentId.value = key.id;
      Object.assign(form, {
        name: key.name,
        allowApi: key.allowApi,
        allowMcp: key.allowMcp,
        allowTerminal: key.allowTerminal,
        allowSchedules: key.allowSchedules,
        allowFiles: key.allowFiles,
        allowTranscode: key.allowTranscode,
        allowGateway: key.allowGateway,
        expiresAt: key.expiresAt ? key.expiresAt.slice(0, 16) : '',
      });
      showModal.value = true;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入密钥名称');

      const payload = {
        name: form.name.trim(),
        allowApi: form.allowApi,
        allowMcp: form.allowMcp,
        allowTerminal: form.allowTerminal,
        allowSchedules: form.allowSchedules,
        allowFiles: form.allowFiles,
        allowTranscode: form.allowTranscode,
        allowGateway: form.allowGateway,
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

    const originUrl = window.location.origin;

    onMounted(load);

    return {
      originUrl,
      keys,
      loading,
      showModal,
      isEdit,
      form,
      showSecretModal,
      createdKeyInfo,
      showGuideModal,
      guideKey,
      load,
      openCreate,
      openEdit,
      save,
      toggleKey,
      removeKey,
      copyToClipboard,
      openGuide,
      formatTime,
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
            对外提供原子级 API Key 授权机制，支持精细控制 REST API 与 MCP (Model Context Protocol) 协议通道以及各个业务模块权限。
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
                <th class="py-2.5 px-3 whitespace-nowrap">状态</th>
                <th class="py-2.5 px-3 whitespace-nowrap">最后使用</th>
                <th class="py-2.5 px-3 whitespace-nowrap">过期时间</th>
                <th class="py-2.5 px-3 text-right whitespace-nowrap">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/40">
              <tr v-for="k in keys" :key="k.id" class="hover:bg-slate-800/20 transition-colors">
                <td class="py-3 px-3 font-medium text-slate-200 whitespace-nowrap">{{ k.name }}</td>
                <td class="py-3 px-3">
                  <code class="px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 font-mono text-cyan-300">
                    {{ k.keyPrefix }}...
                  </code>
                </td>
                <td class="py-3 px-3">
                  <div class="flex flex-wrap gap-1">
                    <span v-if="k.allowApi" class="badge border-cyan-500/50 text-cyan-400 bg-cyan-950/20">API</span>
                    <span v-if="k.allowMcp" class="badge border-purple-500/50 text-purple-400 bg-purple-950/20">MCP</span>
                    <span v-if="!k.allowApi && !k.allowMcp" class="badge border-rose-500/40 text-rose-400 bg-rose-950/20">无通道</span>
                  </div>
                </td>
                <td class="py-3 px-3">
                  <div class="flex flex-wrap gap-1">
                    <span v-if="k.allowTerminal" class="badge border-emerald-500/40 text-emerald-400 bg-emerald-950/20">终端</span>
                    <span v-if="k.allowSchedules" class="badge border-blue-500/40 text-blue-400 bg-blue-950/20">定时任务</span>
                    <span v-if="k.allowFiles" class="badge border-amber-500/40 text-amber-400 bg-amber-950/20">文件/挂载</span>
                    <span v-if="k.allowTranscode" class="badge border-rose-500/40 text-rose-400 bg-rose-950/20">转码</span>
                    <span v-if="k.allowGateway" class="badge border-indigo-500/40 text-indigo-400 bg-indigo-950/20">网关</span>
                  </div>
                </td>
                <td class="py-3 px-3">
                  <span class="badge cursor-pointer"
                        :class="k.isEnabled ? 'border-emerald-500/50 text-emerald-300 bg-emerald-950/30' : 'border-slate-600 text-slate-400 bg-slate-900'"
                        @click="toggleKey(k)">
                    {{ k.isEnabled ? '● 已启用' : '○ 已禁用' }}
                  </span>
                </td>
                <td class="py-3 px-3 text-slate-400">{{ k.lastUsedAt ? formatTime(k.lastUsedAt) : '从未' }}</td>
                <td class="py-3 px-3 text-slate-400 whitespace-nowrap">{{ k.expiresAt ? formatTime(k.expiresAt) : '永久有效' }}</td>
                <td class="py-3 px-3 text-right whitespace-nowrap">
                  <div class="flex items-center justify-end gap-1.5">
                    <button class="btn btn-xs" @click="openGuide(k)">用法</button>
                    <button class="btn btn-xs" @click="openEdit(k)">编辑</button>
                    <button class="btn btn-xs btn-danger" @click="removeKey(k)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 新建/编辑 API Key 模态框 -->
      <div v-if="showModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEdit ? '编辑 API Key 权限' : '新建 API Key' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showModal = false">✕</button>
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
              <label class="block font-medium text-slate-300 mb-2">业务模块细粒度授权 (Module Matrix)</label>
              <div class="grid grid-cols-2 gap-2">
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowTerminal" class="accent-emerald-500" />
                  <span class="text-slate-200">终端指令与会话</span>
                </label>
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="form.allowSchedules" class="accent-blue-500" />
                  <span class="text-slate-200">定时调度与触发</span>
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
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showModal = false">取消</button>
            <button class="btn btn-primary" @click="save()">保存</button>
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

      <!-- 集成指引弹窗 -->
      <div v-if="showGuideModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-xl p-5 flex flex-col gap-4 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">客户端与 MCP 集成指引</h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showGuideModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <span class="font-medium text-slate-300 block mb-1">1. REST API 调用鉴权方式：</span>
              <pre class="p-2.5 rounded bg-slate-950 border border-slate-800 text-slate-300 font-mono text-[11px] overflow-x-auto">curl -H "X-Api-Key: {{ guideKey }}" \
     {{ originUrl }}/api/Overview</pre>
            </div>

            <div>
              <span class="font-medium text-slate-300 block mb-1">2. Claude Desktop / Cursor / Windsurf MCP 配置 (SSE 模式)：</span>
              <pre class="p-2.5 rounded bg-slate-950 border border-slate-800 text-slate-300 font-mono text-[11px] overflow-x-auto">{
  "mcpServers": {
    "linux-web-tool": {
      "url": "{{ originUrl }}/mcp/sse",
      "headers": {
        "X-Api-Key": "{{ guideKey }}"
      }
    }
  }
}</pre>
            </div>
          </div>

          <div class="flex justify-end pt-2">
            <button class="btn" @click="showGuideModal = false">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
