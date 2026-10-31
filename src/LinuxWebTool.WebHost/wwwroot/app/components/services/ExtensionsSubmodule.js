import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';
import ExtensionActionModal from './ExtensionActionModal.js';
import ExtensionCodeModal from './ExtensionCodeModal.js';
import ExtensionConfigModal from './ExtensionConfigModal.js';
import ExtensionInstallGuideModal from './ExtensionInstallGuideModal.js';
import ExtensionLogsModal from './ExtensionLogsModal.js';
import ExtensionRepoImportModal from './ExtensionRepoImportModal.js';
import ExtensionUpdateModal from './ExtensionUpdateModal.js';
import ExtensionZipImportModal from './ExtensionZipImportModal.js';

export default defineComponent({
  name: 'ExtensionsSubmodule',
  components: {
    ExtensionInstallGuideModal, ExtensionActionModal, ExtensionRepoImportModal, ExtensionZipImportModal,
    ExtensionConfigModal, ExtensionUpdateModal, ExtensionLogsModal, ExtensionCodeModal,
  },
  props: {
    env: { type: Object, default: () => ({}) },
  },
  setup() {
    const loading = ref(false);
    const extensions = ref([]);

    // 代码编辑与 Zip 导入
    const showZipModal = ref(false);
    const showCodeModal = ref(false);
    const activeCodeExt = ref(null);

    // 配置弹窗
    const showConfigModal = ref(false);
    const editingExt = ref(null);
    const configFormValues = reactive({});
    const savingConfig = ref(false);

    // 日志与更新
    const showLogsModal = ref(false);
    const logsExtId = ref('');
    const logsContent = ref('');
    const showUpdateModal = ref(false);
    const activeUpdateExt = ref(null);

    // 引导与操作弹窗
    const showGuideModal = ref(false);
    const activeGuideExt = ref(null);
    const showActionModal = ref(false);
    const activeActionExt = ref(null);
    const activeActionDef = ref(null);
    const showRepoModal = ref(false);

    async function loadExtensions() {
      loading.value = true;
      try {
        const res = await http(API.services.extensions);
        if (res.ok && res.data) {
          extensions.value = res.data;
        } else {
          toast.error(res.message || '加载脚本扩展失败');
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function executeAction(extId, actionVerb, desc, target) {
      try {
        const payload = { extensionId: extId, action: actionVerb };
        if (target) payload.target = target;
        const res = await http(API.services.extensionAction, {
          method: 'POST',
          body: payload,
        });
        if (res.ok && res.data?.success) {
          toast.success(`扩展服务 [${extId}] ${desc} 成功`);
          await loadExtensions();
        } else {
          toast.error(res.data?.message || res.message || `${desc} 失败`);
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    function openConfig(ext) {
      editingExt.value = ext;
      for (const k in configFormValues) delete configFormValues[k];
      if (ext.manifest.configForm) {
        for (const f of ext.manifest.configForm) {
          configFormValues[f.field] = ext.currentConfig?.[f.field] || f.defaultValue || '';
        }
      }
      showConfigModal.value = true;
    }

    async function saveConfig() {
      if (!editingExt.value) return;
      savingConfig.value = true;
      try {
        const res = await http(API.services.extensionAction, {
          method: 'POST',
          body: {
            extensionId: editingExt.value.manifest.id,
            action: 'config_set',
            configValues: { ...configFormValues },
          },
        });
        if (res.ok && res.data?.success) {
          toast.success('配置已成功写入并通知扩展');
          showConfigModal.value = false;
          await loadExtensions();
        } else {
          toast.error(res.data?.message || res.message || '保存配置失败');
        }
      } catch (err) {
        toast.error('保存异常: ' + err.message);
      } finally {
        savingConfig.value = false;
      }
    }

    async function viewLogs(extId) {
      logsExtId.value = extId;
      logsContent.value = '正在拉取扩展运行日志...';
      showLogsModal.value = true;
      try {
        const res = await http(API.services.extensionLogs(extId, 150));
        logsContent.value = res.ok && res.data?.logs ? res.data.logs : ('获取日志失败: ' + (res.message || '未知错误'));
      } catch (err) {
        logsContent.value = '获取异常: ' + err.message;
      }
    }

    async function toggleEnabled(ext) {
      const next = !ext.isEnabled;
      try {
        const res = await http(API.services.extensionSetEnabled(ext.manifest.id) + `?enabled=${next}`, {
          method: 'POST',
        });
        if (res.ok) {
          toast.success(`扩展 [${ext.manifest.name}] 已${next ? '启用' : '禁用'}`);
          await loadExtensions();
        } else {
          toast.error(res.message || '切换状态失败');
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    function openUpdate(ext) {
      activeUpdateExt.value = ext;
      showUpdateModal.value = true;
    }

    onMounted(() => {
      loadExtensions();
    });

    return {
      loading,
      extensions,
      showConfigModal,
      editingExt,
      configFormValues,
      savingConfig,
      showLogsModal,
      logsExtId,
      logsContent,
      showGuideModal,
      activeGuideExt,
      showActionModal,
      activeActionExt,
      activeActionDef,
      showRepoModal,
      showUpdateModal,
      activeUpdateExt,
      showZipModal,
      showCodeModal,
      activeCodeExt,
      openCode: (ext) => { activeCodeExt.value = ext; showCodeModal.value = true; },
      loadExtensions,
      executeAction,
      openConfig,
      saveConfig,
      viewLogs,
      openUpdate,
      toggleEnabled,
      openInstallGuide: (ext) => { activeGuideExt.value = ext; showGuideModal.value = true; },
      openCustomAction: (ext, actDef) => { activeActionExt.value = ext; activeActionDef.value = actDef; showActionModal.value = true; },
      openRepoImport: () => { showRepoModal.value = true; },
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 头部控制条 -->
      <div class="panel p-3.5 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-2.5">
          <span class="text-xl">🔌</span>
          <div>
            <div class="flex items-center gap-2">
              <span class="font-display font-semibold text-slate-100 text-sm">动态脚本扩展服务 (Script Extensions)</span>
              <span class="badge border-cyan-500/40 text-cyan-300">契约驱动</span>
            </div>
            <div class="text-xs text-slate-400 mt-0.5">
              内置 SSH 安全守卫 (.sh) 与 Caddy 管理 (.js)，支持环境自检、热更新回滚与 GitHub Mono-repo 隔离导入
            </div>
          </div>
        </div>
        <div class="flex items-center gap-2">
          <button type="button" class="btn btn-xs hover:border-cyan-500/40 text-cyan-400" @click="openRepoImport()">
            🌐 从 GitHub 导入
          </button>
          <button type="button" class="btn btn-xs hover:border-cyan-500/40 text-cyber-primary" @click="showZipModal = true">
            📦 导入 Zip
          </button>
          <button type="button" class="btn btn-xs hover:border-cyan-500/40" :disabled="loading" @click="loadExtensions()">
            🔄 重新扫描插件
          </button>
        </div>
      </div>

      <!-- 扩展卡片流 -->
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">正在扫描与探测动态扩展服务...</div>
      <div v-else-if="extensions.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        当前无可用扩展插件。请在 <code class="font-mono">data/services/extensions/</code> 放置包含 <code class="font-mono">manifest.json</code> 的插件包，或点击上方「从 GitHub 导入」。
      </div>
      <div v-else class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3">
        <div
          v-for="ext in extensions"
          :key="ext.manifest.id"
          class="panel p-4 flex flex-col justify-between gap-3 border-cyber-line/50 hover:border-cyan-500/40 transition-colors overflow-hidden min-w-0"
          :class="{ 'opacity-60 border-slate-700': ext.isEnabled === false, 'border-amber-500/30': ext.status.startsWith('Missing') }"
        >
          <div>
            <!-- 卡片头部 -->
            <div class="flex items-start justify-between gap-2">
              <div class="flex items-center gap-2 min-w-0">
                <span class="text-xl">{{ ext.manifest.icon || '🔌' }}</span>
                <div class="min-w-0">
                  <div class="flex items-center gap-1.5">
                    <span class="font-display font-semibold text-slate-200 text-sm truncate" :title="ext.manifest.name">
                      {{ ext.manifest.name }}
                    </span>
                    <span v-if="ext.isEnabled === false" class="badge text-[10px] border-slate-700 text-slate-400">已禁用</span>
                  </div>
                  <div class="text-[11px] text-slate-500 font-mono">v{{ ext.manifest.version }} · {{ ext.manifest.id }}</div>
                </div>
              </div>
              <span
                class="badge text-[11px] shrink-0"
                :class="[
                  ext.isEnabled === false ? 'border-slate-800 text-slate-500' :
                  ext.status === 'Running' ? 'border-emerald-500/40 text-emerald-300 bg-emerald-500/10' :
                  ext.status === 'Stopped' ? 'border-slate-700 text-slate-500' :
                  ext.status === 'MissingRuntime' ? 'border-amber-500/50 text-amber-300 bg-amber-500/10' :
                  ext.status === 'MissingBinary' ? 'border-amber-500/50 text-amber-300 bg-amber-500/10' :
                  ext.status === 'Degraded' ? 'border-rose-500/40 text-rose-300 bg-rose-500/10' : ''
                ]"
              >
                {{
                  ext.isEnabled === false ? '已禁用' :
                  ext.status === 'Running' ? '运行中' :
                  ext.status === 'MissingRuntime' ? '⚠️ 缺少运行时' :
                  ext.status === 'MissingBinary' ? '⚠️ 缺少目标程序' :
                  ext.status === 'Degraded' ? '异常' : '已停止'
                }}
              </span>
            </div>

            <div class="text-xs text-slate-400 mt-2 line-clamp-2" :title="ext.manifest.description">
              {{ ext.manifest.description || '无详细描述' }}
            </div>

            <!-- 缺失依赖引导 -->
            <div v-if="ext.status.startsWith('Missing')" class="mt-2.5 p-2 rounded bg-amber-500/10 border border-amber-500/30 flex items-center justify-between gap-2">
              <div class="text-[11px] text-amber-300 font-mono truncate">{{ ext.statusMessage || '环境依赖不满足' }}</div>
              <button type="button" class="text-[11px] text-cyan-400 hover:text-cyan-300 shrink-0 underline" @click="openInstallGuide(ext)">
                🛠️ 安装引导
              </button>
            </div>
            <div v-else-if="ext.statusMessage" class="mt-2 p-1.5 rounded bg-black/25 text-[11px] font-mono text-cyan-300/80 truncate">
              提示: {{ ext.statusMessage }}
            </div>

            <!-- 自定义 Actions -->
            <div v-if="ext.manifest.customActions?.length && !ext.status.startsWith('Missing') && ext.isEnabled !== false" class="mt-2.5 pt-2 border-t border-cyber-line/30 flex flex-wrap gap-1.5">
              <button
                v-for="act in ext.manifest.customActions"
                :key="act.action"
                type="button"
                class="btn btn-xs hover:border-cyan-500/40 text-[11px] py-0.5 px-2 flex items-center gap-1"
                @click="openCustomAction(ext, act)"
              >
                <span>{{ act.icon || '⚙️' }}</span>
                <span>{{ act.label }}</span>
              </button>
            </div>
          </div>

          <!-- 底部操作按钮 -->
          <div class="flex flex-wrap items-center justify-between border-t border-cyber-line/40 pt-2.5 gap-y-2 gap-x-1">
            <div class="flex flex-wrap items-center gap-1">
              <button
                v-if="ext.status !== 'Running'"
                type="button"
                class="btn btn-xs border-emerald-500/40 text-emerald-300 hover:bg-emerald-500/20"
                :disabled="ext.status.startsWith('Missing') || ext.isEnabled === false"
                @click="executeAction(ext.manifest.id, 'start', '启动', ext.manifest?.target || ext.target)"
              >
                启动
              </button>
              <button
                v-else
                type="button"
                class="btn btn-xs border-rose-500/40 text-rose-300 hover:bg-rose-500/20"
                @click="executeAction(ext.manifest.id, 'stop', '停止', ext.manifest?.target || ext.target)"
              >
                停止
              </button>
              <button
                type="button"
                class="btn btn-xs hover:border-cyan-500/40"
                :disabled="ext.status.startsWith('Missing') || ext.isEnabled === false"
                @click="executeAction(ext.manifest.id, 'restart', '重启', ext.manifest?.target || ext.target)"
              >
                重启
              </button>
            </div>

            <div class="flex flex-wrap items-center gap-1 justify-end">
              <button
                type="button"
                class="btn btn-xs hover:border-cyan-500/40"
                :class="ext.isEnabled === false ? 'text-emerald-400' : 'text-slate-400'"
                :title="ext.isEnabled === false ? '点击启用该扩展' : '点击禁用该扩展'"
                @click="toggleEnabled(ext)"
              >
                {{ ext.isEnabled === false ? '启用' : '禁用' }}
              </button>
              <button
                type="button"
                class="btn btn-xs hover:border-cyan-500/40 text-cyan-300"
                title="检查版本与安全热更新"
                @click="openUpdate(ext)"
              >
                更新
              </button>
              <button
                v-if="ext.manifest.configForm?.length"
                type="button"
                class="btn btn-xs hover:border-cyan-500/40"
                :disabled="ext.status.startsWith('Missing')"
                @click="openConfig(ext)"
              >
                配置
              </button>
              <button
                v-if="ext.manifest.capabilities?.logs"
                type="button"
                class="btn btn-xs hover:border-cyan-500/40"
                @click="viewLogs(ext.manifest.id)"
              >
                日志
              </button>
              <button
                type="button"
                class="btn btn-xs hover:border-amber-500/40 text-amber-300"
                title="在线编辑代码与调试"
                @click="openCode(ext)"
              >
                编辑
              </button>
            </div>
          </div>
        </div>
      </div>

      <ExtensionConfigModal :show="showConfigModal" :ext="editingExt" :form-values="configFormValues" :saving="savingConfig" @close="showConfigModal = false" @save="saveConfig()" />
      <ExtensionLogsModal :show="showLogsModal" :ext-id="logsExtId" :logs="logsContent" @close="showLogsModal = false" />
      <ExtensionInstallGuideModal :show="showGuideModal" :ext="activeGuideExt" :env="env" @close="showGuideModal = false" />
      <ExtensionActionModal :show="showActionModal" :ext="activeActionExt" :action-def="activeActionDef" :env="env" @close="showActionModal = false" @refresh="loadExtensions()" />
      <ExtensionRepoImportModal :show="showRepoModal" @close="showRepoModal = false" @imported="loadExtensions()" />
      <ExtensionUpdateModal :show="showUpdateModal" :ext="activeUpdateExt" @close="showUpdateModal = false" @updated="loadExtensions()" />
      <ExtensionZipImportModal :show="showZipModal" @close="showZipModal = false" @imported="loadExtensions()" />
      <ExtensionCodeModal :show="showCodeModal" :extension-id="activeCodeExt?.manifest?.id" :extension-name="activeCodeExt?.manifest?.name" @close="showCodeModal = false" @saved="loadExtensions()" />
    </div>
  `,
});
