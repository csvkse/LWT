import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'SshSubmodule',
  setup() {
    const loading = ref(false);
    const saving = ref(false);
    const config = reactive({
      isInstalled: false,
      isRunning: false,
      port: 22,
      permitRootLogin: 'prohibit-password',
      passwordAuthentication: true,
      pubkeyAuthentication: true,
      configFilePath: '',
      authorizedKeys: [],
    });

    const newKey = ref('');

    async function loadConfig() {
      loading.value = true;
      try {
        const res = await http(API.services.sshConfig, { method: 'GET' });
        if (res.ok && res.data) {
          Object.assign(config, res.data);
        } else {
          toast.error(res.message || '加载 SSH 配置失败');
        }
      } catch (err) {
        toast.error('请求异常: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function saveConfig() {
      saving.value = true;
      try {
        const res = await http(API.services.sshConfig, {
          method: 'POST',
          body: {
            port: Number(config.port),
            permitRootLogin: config.permitRootLogin,
            passwordAuthentication: Boolean(config.passwordAuthentication),
            pubkeyAuthentication: Boolean(config.pubkeyAuthentication),
            authorizedKeys: config.authorizedKeys,
          },
        });
        if (res.ok && res.data?.success) {
          toast.success('SSH 配置已安全更新并重载服务');
          await loadConfig();
        } else {
          toast.error(res.data?.message || res.message || '保存配置失败');
        }
      } catch (err) {
        toast.error('保存失败: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    function addKey() {
      const k = newKey.value.trim();
      if (!k) return;
      if (config.authorizedKeys.includes(k)) {
        toast.warning('该公钥已存在');
        return;
      }
      config.authorizedKeys.push(k);
      newKey.value = '';
    }

    function removeKey(idx) {
      config.authorizedKeys.splice(idx, 1);
    }

    onMounted(() => {
      loadConfig();
    });

    return {
      loading,
      saving,
      config,
      newKey,
      loadConfig,
      saveConfig,
      addKey,
      removeKey,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 状态与保存提示 -->
      <div class="panel p-3.5 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-2.5">
          <span class="text-xl">🔒</span>
          <div>
            <div class="flex items-center gap-2">
              <span class="font-display font-semibold text-slate-100 text-sm">OpenSSH 服务配置</span>
              <span class="badge" :class="config.isRunning ? 'border-emerald-500/40 text-emerald-300' : 'border-slate-700 text-slate-500'">
                {{ config.isRunning ? '服务运行中' : '服务未激活' }}
              </span>
            </div>
            <div class="text-xs text-slate-400 mt-0.5">
              配置文件路径: <code class="font-mono text-cyan-300/80">{{ config.configFilePath || '/etc/ssh/sshd_config' }}</code>
            </div>
          </div>
        </div>
        <div class="flex items-center gap-2">
          <button type="button" class="btn btn-xs" :disabled="loading || saving" @click="loadConfig()">
            🔄 重新加载
          </button>
          <button type="button" class="btn btn-xs btn-primary" :disabled="loading || saving" @click="saveConfig()">
            {{ saving ? '安全校验保存中...' : '💾 保存配置' }}
          </button>
        </div>
      </div>

      <!-- 参数配置面板 -->
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <div class="panel p-4 flex flex-col gap-3.5">
          <div class="font-display font-medium text-slate-200 text-xs border-b border-cyber-line/50 pb-2">
            核心监听与认证策略
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">SSH 监听端口 (Port)</label>
            <input type="number" v-model.number="config.port" min="1" max="65535" class="input text-xs w-full" />
            <span class="text-[11px] text-slate-500 mt-0.5 block">默认 22 端口。修改后请确认防火墙已放行新端口。</span>
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">Root 用户登录权限 (PermitRootLogin)</label>
            <select v-model="config.permitRootLogin" class="input text-xs w-full">
              <option value="prohibit-password">仅密钥登录 (prohibit-password，推荐)</option>
              <option value="yes">允许密码与密钥登录 (yes)</option>
              <option value="no">完全禁止 Root 登录 (no)</option>
            </select>
          </div>

          <div class="flex items-center justify-between pt-1">
            <div>
              <div class="text-xs font-medium text-slate-200">密码身份认证 (PasswordAuthentication)</div>
              <div class="text-[11px] text-slate-500">是否允许通过用户名和密码连接服务器</div>
            </div>
            <input type="checkbox" v-model="config.passwordAuthentication" class="toggle" />
          </div>

          <div class="flex items-center justify-between pt-1">
            <div>
              <div class="text-xs font-medium text-slate-200">公钥认证 (PubkeyAuthentication)</div>
              <div class="text-[11px] text-slate-500">是否允许使用 SSH Key 密钥免密登录</div>
            </div>
            <input type="checkbox" v-model="config.pubkeyAuthentication" class="toggle" />
          </div>
        </div>

        <!-- 密钥管理 -->
        <div class="panel p-4 flex flex-col gap-3">
          <div class="font-display font-medium text-slate-200 text-xs border-b border-cyber-line/50 pb-2 flex items-center justify-between">
            <span>免密授权公钥 (authorized_keys)</span>
            <span class="text-[11px] text-slate-400 font-mono">{{ config.authorizedKeys?.length || 0 }} 个公钥</span>
          </div>

          <!-- 添加新公钥 -->
          <div class="flex items-center gap-2">
            <input
              type="text"
              v-model="newKey"
              placeholder="粘贴客户端公钥 (ssh-rsa / ssh-ed25519)..."
              class="input text-xs flex-1 font-mono"
            />
            <button type="button" class="btn btn-xs btn-primary shrink-0" @click="addKey()">
              ➕ 添加
            </button>
          </div>

          <!-- 已有公钥列表 -->
          <div class="space-y-2 overflow-y-auto max-h-[16rem]">
            <div
              v-for="(k, idx) in config.authorizedKeys"
              :key="idx"
              class="bg-black/30 rounded p-2 text-[11px] font-mono border border-cyber-line/30 flex items-start justify-between gap-2"
            >
              <span class="truncate text-cyan-300/80 flex-1 select-all" :title="k">{{ k }}</span>
              <button type="button" class="text-rose-400 hover:text-rose-300 shrink-0 px-1" @click="removeKey(idx)">
                ✕
              </button>
            </div>
            <div v-if="!config.authorizedKeys || config.authorizedKeys.length === 0" class="text-slate-500 text-xs text-center py-4">
              暂无免密公钥。
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
