import { computed, defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginActionModal',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
    plugin: {
      type: Object,
      default: null,
    },
    actionDef: {
      type: Object,
      default: null,
    },
  },
  emits: ['close', 'refresh'],
  setup(props, { emit }) {
    const executing = ref(false);
    const formValues = ref({});
    const executionResult = ref(null);
    const operationStatus = ref(null);
    let pollTimer = null;

    watch(
      () => [props.show, props.actionDef],
      ([show, action]) => {
        if (show && action) {
          executionResult.value = null;
          operationStatus.value = null;
          const initial = {};
          if (Array.isArray(action.fields)) {
            for (const f of action.fields) {
              initial[f.name] = f.defaultValue !== undefined ? f.defaultValue : '';
            }
          }
          formValues.value = initial;
        } else {
          stopPolling();
        }
      },
      { immediate: true }
    );

    function stopPolling() {
      if (pollTimer) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
    }

    async function pollOperation(opId) {
      try {
        const res = await http(API.pluginOperations.item(opId));
        if (res.ok && res.data) {
          operationStatus.value = res.data;
          if (res.data.status === 'succeeded' || res.data.status === 'failed' || res.data.status === 'canceled') {
            stopPolling();
            executing.value = false;
            emit('refresh');
          }
        }
      } catch (err) {
        stopPolling();
        executing.value = false;
        toast.error('查询操作状态失败: ' + err.message);
      }
    }

    async function handleExecute() {
      if (!props.plugin || !props.actionDef) return;
      executing.value = true;
      executionResult.value = null;
      operationStatus.value = null;

      try {
        const actionVerb = props.actionDef.action || props.actionDef.name;
        const paramVal = props.actionDef.requireParam
          ? (formValues.value.param ?? '')
          : (formValues.value.param ?? '');
        const payload = {
          pluginId: props.plugin.id,
          action: actionVerb,
          param: paramVal,
        };
        const res = await http(API.plugins.action, {
          method: 'POST',
          body: payload,
        });

        if (res.ok && res.data) {
          executionResult.value = res.data;
          if (res.data.operationId) {
            pollOperation(res.data.operationId);
            pollTimer = setInterval(() => pollOperation(res.data.operationId), 1500);
          } else {
            executing.value = false;
            const isOk = res.data.status === 'succeeded' || res.data.success;
            if (isOk) {
              toast.success(`动作 [${props.actionDef.label || actionVerb}] 执行完成`);
              emit('refresh');
            } else {
              toast.error(res.data.message || res.data.error || '动作执行失败');
            }
          }
        } else {
          executing.value = false;
          toast.error(res.message || '执行动作失败');
        }
      } catch (err) {
        executing.value = false;
        toast.error('执行动作异常: ' + err.message);
      }
    }

    function handleClose() {
      stopPolling();
      emit('close');
    }

    function getArtifactDownloadUrl(artifactId) {
      if (!operationStatus.value?.operationId) return '';
      return API.pluginOperations.artifactDownload(operationStatus.value.operationId, artifactId);
    }

    function stripAnsi(text) {
      if (!text) return '';
      return String(text)
        .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)/g, '')
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
        .replace(/\]133;[A-Za-z0-9;=._-]*/g, '')
        .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, '')
        .trim();
    }

    const formattedOutput = computed(() => {
      const raw = executionResult.value?.output || executionResult.value?.message || '';
      return stripAnsi(raw) || '（无输出）';
    });

    return {
      executing,
      formValues,
      executionResult,
      operationStatus,
      formattedOutput,
      handleExecute,
      handleClose,
      getArtifactDownloadUrl,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-xl p-5 border border-cyan-500/30 flex flex-col max-h-[90vh]">
        <!-- 弹窗头部 -->
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line/50">
          <div class="flex items-center gap-2">
            <span class="text-xl">{{ actionDef?.icon || '⚡' }}</span>
            <div>
              <h3 class="font-display font-semibold text-slate-100 text-sm">
                {{ actionDef?.label || actionDef?.name || actionDef?.action }}
              </h3>
              <div class="text-xs text-slate-400 font-mono">
                插件: {{ plugin?.name }} ({{ plugin?.id }}) · 动作: {{ actionDef?.action || actionDef?.name }}
              </div>
            </div>
          </div>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-white" @click="handleClose()">
            ✕
          </button>
        </div>

        <!-- 动作描述与参数表单 -->
        <div class="py-4 space-y-4 overflow-y-auto flex-1">
          <div v-if="actionDef?.description" class="text-xs text-slate-300 bg-slate-900/60 p-2.5 rounded border border-cyber-line/40">
            {{ actionDef.description }}
          </div>

          <!-- 单一必须参数（用于 ban_ip 等动作） -->
          <div v-if="actionDef?.requireParam" class="space-y-1.5">
            <label class="text-xs text-slate-300 font-semibold flex items-center justify-between">
              <span>输入参数:</span>
              <span class="text-amber-400 text-[10px]">* 必需参数</span>
            </label>
            <input
              v-model="formValues.param"
              type="text"
              class="input-base text-xs w-full font-mono text-cyan-300"
              :placeholder="actionDef?.paramPlaceholder || '请输入执行参数...'"
            />
          </div>

          <!-- 多字段参数定义（如果存在） -->
          <div v-else-if="actionDef?.fields && actionDef.fields.length > 0" class="space-y-3">
            <div class="text-xs font-semibold text-slate-300">参数配置:</div>
            <div v-for="field in actionDef.fields" :key="field.name" class="space-y-1">
              <label class="text-xs text-slate-300 flex items-center justify-between">
                <span>{{ field.label || field.name }}</span>
                <span v-if="field.required" class="text-red-400 text-[10px]">* 必填</span>
              </label>

              <!-- 布尔开关 -->
              <div v-if="field.type === 'boolean'" class="flex items-center gap-2">
                <input
                  v-model="formValues[field.name]"
                  type="checkbox"
                  class="rounded bg-slate-900 border-slate-700 text-cyan-500 focus:ring-cyan-500"
                />
                <span class="text-xs text-slate-400">{{ field.description || '启用选项' }}</span>
              </div>

              <!-- 下拉选项 -->
              <select
                v-else-if="field.type === 'select'"
                v-model="formValues[field.name]"
                class="input-base text-xs w-full"
              >
                <option v-for="opt in field.options" :key="opt.value" :value="opt.value">
                  {{ opt.label || opt.value }}
                </option>
              </select>

              <!-- 普通文本或数字 -->
              <input
                v-else
                v-model="formValues[field.name]"
                :type="field.type === 'number' ? 'number' : 'text'"
                class="input-base text-xs w-full"
                :placeholder="field.placeholder || ''"
              />
              <div v-if="field.description && field.type !== 'boolean'" class="text-[11px] text-slate-500">
                {{ field.description }}
              </div>
            </div>
          </div>
          <div v-else class="text-xs text-slate-400 italic">
            此动作无需额外输入参数，点击下方按钮立即触发执行。
          </div>

          <!-- 异步操作进度与产物 -->
          <div v-if="operationStatus" class="p-3 rounded bg-slate-900/80 border border-cyber-line space-y-2">
            <div class="flex items-center justify-between text-xs">
              <span class="text-slate-400">异步任务状态:</span>
              <span class="font-medium"
                :class="{
                  'text-emerald-400': operationStatus.status === 'succeeded',
                  'text-amber-400': operationStatus.status === 'running' || operationStatus.status === 'queued',
                  'text-red-400': operationStatus.status === 'failed',
                  'text-slate-400': operationStatus.status === 'canceled',
                }"
              >
                {{ operationStatus.status }}
              </span>
            </div>
            <div v-if="operationStatus.progress !== undefined" class="w-full bg-slate-800 rounded-full h-1.5 overflow-hidden">
              <div class="bg-cyan-500 h-1.5 transition-all duration-300" :style="{ width: operationStatus.progress + '%' }"></div>
            </div>
            <div v-if="operationStatus.artifactRefs && operationStatus.artifactRefs.length > 0" class="pt-2 border-t border-slate-800">
              <div class="text-[11px] text-slate-400 mb-1">生成产物:</div>
              <div class="flex flex-wrap gap-1.5">
                <a
                  v-for="art in operationStatus.artifactRefs"
                  :key="art.id"
                  :href="getArtifactDownloadUrl(art.id)"
                  target="_blank"
                  class="badge text-[10px] bg-cyan-950/40 border-cyan-700/50 text-cyan-300 hover:underline"
                >
                  📥 {{ art.filename || art.id }}
                </a>
              </div>
            </div>
          </div>

          <!-- 同步输出日志结果 -->
          <div v-if="executionResult" class="space-y-1">
            <div class="flex items-center justify-between text-xs">
              <span class="text-slate-400">执行结果:</span>
              <span class="text-slate-400 font-mono text-[10px]">
                耗时: {{ executionResult.durationMs || 0 }}ms | 退出码: {{ executionResult.exitCode ?? 0 }}
              </span>
            </div>
            <pre class="p-2.5 rounded bg-slate-950 border border-cyber-line/60 font-mono text-xs text-slate-300 overflow-x-auto max-h-48 whitespace-pre-wrap">{{ formattedOutput }}</pre>
          </div>
        </div>

        <!-- 底部控制按钮 -->
        <div class="pt-3 border-t border-cyber-line/50 flex items-center justify-end gap-2">
          <button type="button" class="btn-ghost text-xs" :disabled="executing" @click="handleClose()">
            关闭
          </button>
          <button
            type="button"
            class="btn-primary text-xs flex items-center gap-1.5"
            :disabled="executing"
            @click="handleExecute()"
          >
            <span v-if="executing" class="animate-spin">⏳</span>
            <span>{{ executing ? '执行中...' : '立即执行' }}</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
