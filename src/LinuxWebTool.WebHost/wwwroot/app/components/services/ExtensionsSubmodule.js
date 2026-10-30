import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionsSubmodule',
  setup() {
    const loading = ref(false);
    const extensions = ref([]);

    // 配置弹窗
    const showConfigModal = ref(false);
    const editingExt = ref(null);
    const configFormValues = reactive({});
    const savingConfig = ref(false);

    // 日志弹窗
    const showLogsModal = ref(false);
    const logsExtId = ref('');
    const logsContent = ref('');
    const logsLoading = ref(false);

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

    async function executeAction(extId, actionVerb, desc) {
      try {
        const res = await http(API.services.extensionAction, {
          method: 'POST',
          body: { extensionId: extId, action: actionVerb },
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
      logsLoading.value = true;
      showLogsModal.value = true;
      try {
        const res = await http(API.services.extensionLogs(extId, 150));
        if (res.ok && res.data?.logs) {
          logsContent.value = res.data.logs;
        } else {
          logsContent.value = '获取日志失败: ' + (res.message || '未知错误');
        }
      } catch (err) {
        logsContent.value = '获取异常: ' + err.message;
      } finally {
        logsLoading.value = false;
      }
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
      logsLoading,
      loadExtensions,
      executeAction,
      openConfig,
      saveConfig,
      viewLogs,
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
              目录: <code class="font-mono text-cyan-300/80">data/services/extensions/</code> (支持 Bash / PowerShell / Python / Node.js 钩子)
            </div>
          </div>
        </div>
        <button type="button" class="btn btn-xs hover:border-cyan-500/40" :disabled="loading" @click="loadExtensions()">
          🔄 重新扫描插件
        </button>
      </div>

      <!-- 扩展卡片流 -->
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
        正在扫描与探测动态扩展服务...
      </div>
      <div v-else-if="extensions.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        当前无可用扩展插件。请在 <code class="font-mono">data/services/extensions/</code> 放置包含 <code class="font-mono">manifest.json</code> 的服务插件包。
      </div>
      <div v-else class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3">
        <div
          v-for="ext in extensions"
          :key="ext.manifest.id"
          class="panel p-4 flex flex-col justify-between gap-3 border-cyber-line/50 hover:border-cyan-500/40 transition-colors"
        >
          <div>
            <!-- 卡片头部 -->
            <div class="flex items-start justify-between gap-2">
              <div class="flex items-center gap-2 min-w-0">
                <span class="text-xl">{{ ext.manifest.icon || '🔌' }}</span>
                <div class="min-w-0">
                  <div class="font-display font-semibold text-slate-200 text-sm truncate" :title="ext.manifest.name">
                    {{ ext.manifest.name }}
                  </div>
                  <div class="text-[11px] text-slate-500 font-mono">v{{ ext.manifest.version }} · {{ ext.manifest.id }}</div>
                </div>
              </div>
              <span
                class="badge text-[11px] shrink-0"
                :class="ext.status === 'Running' ? 'border-emerald-500/40 text-emerald-300 bg-emerald-500/10' : 'border-slate-700 text-slate-500'"
              >
                {{ ext.status === 'Running' ? '运行中' : '已停止' }}
              </span>
            </div>

            <div class="text-xs text-slate-400 mt-2 line-clamp-2" :title="ext.manifest.description">
              {{ ext.manifest.description || '无详细描述' }}
            </div>

            <div v-if="ext.statusMessage" class="mt-2 p-1.5 rounded bg-black/25 text-[11px] font-mono text-cyan-300/80 truncate">
              提示: {{ ext.statusMessage }}
            </div>
          </div>

          <!-- 底部操作按钮 -->
          <div class="flex items-center justify-between border-t border-cyber-line/40 pt-2.5 gap-1.5">
            <div class="flex items-center gap-1">
              <button
                v-if="ext.status !== 'Running'"
                type="button"
                class="btn btn-xs border-emerald-500/40 text-emerald-300 hover:bg-emerald-500/20"
                @click="executeAction(ext.manifest.id, 'start', '启动')"
              >
                启动
              </button>
              <button
                v-else
                type="button"
                class="btn btn-xs border-rose-500/40 text-rose-300 hover:bg-rose-500/20"
                @click="executeAction(ext.manifest.id, 'stop', '停止')"
              >
                停止
              </button>
              <button
                type="button"
                class="btn btn-xs hover:border-cyan-500/40"
                @click="executeAction(ext.manifest.id, 'restart', '重启')"
              >
                重启
              </button>
            </div>
            <div class="flex items-center gap-1">
              <button
                v-if="ext.manifest.configForm?.length > 0"
                type="button"
                class="btn btn-xs"
                @click="openConfig(ext)"
                title="修改参数配置"
              >
                参数
              </button>
              <button
                type="button"
                class="btn btn-xs"
                @click="viewLogs(ext.manifest.id)"
                title="查看运行日志"
              >
                日志
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- 参数配置弹窗 -->
      <div v-if="showConfigModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-md w-full flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">配置: {{ editingExt?.manifest?.name }}</span>
            <button type="button" class="btn btn-xs" @click="showConfigModal = false">✕ 关闭</button>
          </div>
          <div class="p-4 space-y-3 max-h-[60vh] overflow-y-auto">
            <div v-for="field in editingExt?.manifest?.configForm" :key="field.field">
              <label class="block text-xs text-slate-400 mb-1">{{ field.label }}</label>
              <input
                v-if="field.type === 'number'"
                type="number"
                v-model="configFormValues[field.field]"
                class="input text-xs w-full"
              />
              <input
                v-else-if="field.type === 'password'"
                type="password"
                v-model="configFormValues[field.field]"
                class="input text-xs w-full font-mono"
              />
              <input
                v-else
                type="text"
                v-model="configFormValues[field.field]"
                class="input text-xs w-full"
              />
            </div>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2">
            <button type="button" class="btn btn-xs" @click="showConfigModal = false">取消</button>
            <button type="button" class="btn btn-xs btn-primary" :disabled="savingConfig" @click="saveConfig()">
              {{ savingConfig ? '保存中...' : '💾 保存并应用' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 日志弹窗 -->
      <div v-if="showLogsModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-4xl w-full max-h-[85vh] flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">扩展服务日志: {{ logsExtId }}</span>
            <button type="button" class="btn btn-xs" @click="showLogsModal = false">✕ 关闭</button>
          </div>
          <div class="p-4 flex-1 overflow-auto bg-black/50">
            <pre class="font-mono text-xs text-slate-300 whitespace-pre-wrap leading-relaxed select-text">{{ logsContent }}</pre>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end">
            <button type="button" class="btn btn-xs btn-primary" @click="showLogsModal = false">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
