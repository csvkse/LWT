import { computed, defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginInvokeDrawer',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
    plugin: {
      type: Object,
      default: null,
    },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const loadingServices = ref(false);
    const invoking = ref(false);
    const services = ref([]);
    const selectedServiceId = ref('');
    const selectedOperation = ref('');
    const requestPayload = ref('{}');
    const invokeResult = ref(null);

    const currentService = computed(() => {
      return services.value.find((s) => s.id === selectedServiceId.value) || null;
    });

    const availableOperations = computed(() => {
      if (!currentService.value) return [];
      const ops = currentService.value.operations || currentService.value.actions || [];
      return Array.isArray(ops) ? ops : [];
    });

    async function loadDiscoveredServices() {
      loadingServices.value = true;
      try {
        const res = await http(API.plugins.discovery, { method: 'GET' });
        if (res && res.ok && Array.isArray(res.data)) {
          services.value = res.data;
          // 若有传入 plugin，优先匹配
          if (props.plugin && props.plugin.id) {
            const found = services.value.find((s) => s.pluginId === props.plugin.id || s.id === props.plugin.id);
            if (found) {
              selectedServiceId.value = found.id;
            } else if (services.value.length > 0) {
              selectedServiceId.value = services.value[0].id;
            }
          } else if (services.value.length > 0 && !selectedServiceId.value) {
            selectedServiceId.value = services.value[0].id;
          }
          autoSelectFirstOperation();
        }
      } catch (err) {
        toast.error('加载服务发现清单失败: ' + err.message);
      } finally {
        loadingServices.value = false;
      }
    }

    function autoSelectFirstOperation() {
      if (availableOperations.value.length > 0) {
        selectOperation(availableOperations.value[0]);
      } else {
        selectedOperation.value = '';
        requestPayload.value = '{}';
      }
    }

    function selectOperation(op) {
      selectedOperation.value = op;
      // 预填充默认参数示例
      if (op === 'add') {
        requestPayload.value = JSON.stringify({ a: 15, b: 27 }, null, 2);
      } else if (op === 'multiply') {
        requestPayload.value = JSON.stringify({ a: 6, b: 7 }, null, 2);
      } else if (op === 'fibonacci' || op === 'fib') {
        requestPayload.value = JSON.stringify({ n: 10 }, null, 2);
      } else if (op === 'calc') {
        requestPayload.value = JSON.stringify({ op: 'add', a: 100, b: 200 }, null, 2);
      } else {
        requestPayload.value = JSON.stringify({ action: op, timestamp: Date.now() }, null, 2);
      }
    }

    async function handleInvoke() {
      if (!selectedServiceId.value) {
        toast.error('请选择调用的目标服务');
        return;
      }
      if (!selectedOperation.value) {
        toast.error('请指定调用的 Operation 或 Action');
        return;
      }

      invoking.value = true;
      invokeResult.value = null;

      try {
        const payloadStr = requestPayload.value?.trim() || '{}';
        const targetPluginId = currentService.value?.pluginId || selectedServiceId.value;
        const res = await http(API.plugins.invoke, {
          method: 'POST',
          body: {
            pluginId: targetPluginId,
            actionOrPath: selectedOperation.value,
            payload: payloadStr,
          },
        });

        if (res && res.ok && res.data) {
          invokeResult.value = res.data;
          if (res.data.success) {
            toast.success(`调用成功 (${res.data.latencyMs?.toFixed(2) || 0}ms)`);
          } else {
            toast.error(res.data.errorMessage || res.data.error || '执行返回异常');
          }
        } else {
          toast.error(res?.message || '透明网关调用失败');
          invokeResult.value = {
            success: false,
            statusCode: 500,
            errorMessage: res?.message || '请求失败',
            durationMs: 0,
          };
        }
      } catch (err) {
        toast.error('透明调用异常: ' + err.message);
        invokeResult.value = {
          success: false,
          statusCode: 500,
          errorMessage: err.message,
          durationMs: 0,
        };
      } finally {
        invoking.value = false;
      }
    }

    watch(
      () => props.show,
      (val) => {
        if (val) {
          invokeResult.value = null;
          loadDiscoveredServices();
        }
      }
    );

    watch(
      () => selectedServiceId.value,
      () => {
        autoSelectFirstOperation();
      }
    );

    function close() {
      emit('close');
    }

    return {
      loadingServices,
      invoking,
      services,
      selectedServiceId,
      selectedOperation,
      requestPayload,
      invokeResult,
      currentService,
      availableOperations,
      selectOperation,
      handleInvoke,
      close,
    };
  },
  template: `
    <div
      v-if="show"
      class="fixed inset-0 z-50 flex justify-end bg-slate-950/70 backdrop-blur-xs transition-opacity"
      @click.self="close()"
    >
      <div class="w-full max-w-xl bg-slate-900 border-l border-cyber-line/80 h-full flex flex-col shadow-2xl">
        <!-- 抽屉头部 -->
        <div class="p-4 border-b border-cyber-line/60 flex items-center justify-between shrink-0 bg-slate-900/90">
          <div class="flex items-center gap-2">
            <span class="text-xl">📡</span>
            <div>
              <h2 class="text-sm font-semibold text-slate-100 flex items-center gap-2">
                <span>微服务与微函数透明互调</span>
                <span class="text-[10px] px-1.5 py-0.5 rounded bg-cyan-500/20 text-cyan-300 font-mono border border-cyan-500/40">
                  Universal RPC
                </span>
              </h2>
              <p class="text-xs text-slate-400 mt-0.5">跨协议动态发现与零代理反射调用网关</p>
            </div>
          </div>
          <button
            type="button"
            class="text-slate-400 hover:text-white p-1 rounded transition-colors text-base"
            @click="close()"
          >
            ✕
          </button>
        </div>

        <!-- 抽屉滚动内容区 (受限容器高度) -->
        <div class="flex-1 min-h-0 overflow-y-auto p-4 space-y-4">
          <!-- 目标服务选择与元数据透出 -->
          <div class="panel p-3 border border-cyber-line/60 space-y-2.5">
            <label class="block text-xs font-medium text-slate-300">目标插件服务 (Service Target)</label>
            <div class="flex items-center gap-2">
              <select
                v-model="selectedServiceId"
                class="input select-compact text-xs py-1.5 px-2 bg-slate-950 border border-cyber-line/80 text-slate-100 focus:border-cyan-400 rounded-md flex-1"
              >
                <option v-for="s in services" :key="s.id" :value="s.id">
                  {{ s.name }} ({{ s.id }}) - [{{ s.protocol || 'Default' }}]
                </option>
              </select>
            </div>

            <!-- 当前选中服务明细 -->
            <div v-if="currentService" class="text-[11px] grid grid-cols-2 gap-2 pt-2 border-t border-cyber-line/40 text-slate-400 font-mono">
              <div>协议: <span class="text-cyan-300 font-medium">{{ currentService.protocol || 'Default' }}</span></div>
              <div>生命周期: <span class="text-purple-300 font-medium">{{ currentService.lifecycle || 'Default' }}</span></div>
              <div v-if="currentService.port">监听端口: <span class="text-amber-300">:{{ currentService.port }}</span></div>
              <div v-if="currentService.endpoint">端点: <span class="text-slate-200 truncate">{{ currentService.endpoint }}</span></div>
            </div>
          </div>

          <!-- Operation 选择 -->
          <div class="panel p-3 border border-cyber-line/60 space-y-2.5">
            <label class="block text-xs font-medium text-slate-300 flex items-center justify-between">
              <span>调用接口 / 操作 (Operation)</span>
              <span class="text-[10px] text-slate-400 font-mono">{{ availableOperations.length }} 个导出方法</span>
            </label>

            <div v-if="availableOperations.length > 0" class="flex flex-wrap gap-1.5">
              <button
                v-for="op in availableOperations"
                :key="op"
                type="button"
                class="px-2.5 py-1 text-xs rounded font-mono transition-colors border"
                :class="selectedOperation === op ? 'bg-cyan-500/20 text-cyan-300 border-cyan-500/50' : 'bg-slate-800 text-slate-300 border-slate-700 hover:border-slate-600'"
                @click="selectOperation(op)"
              >
                {{ op }}
              </button>
            </div>
            <div v-else class="text-xs text-slate-500 italic">
              该插件未显式导出 Operations 列表，可手动输入操作路径。
            </div>

            <input
              v-model="selectedOperation"
              type="text"
              class="input text-xs py-1.5 px-2 bg-slate-950 border border-cyber-line/80 text-slate-100 font-mono rounded-md w-full focus:border-cyan-400"
              placeholder="例如: add / calc / ping / status"
            />
          </div>

          <!-- 请求载荷 JSON 编辑 -->
          <div class="panel p-3 border border-cyber-line/60 space-y-2">
            <div class="flex items-center justify-between text-xs text-slate-300">
              <span class="font-medium">请求载荷 (JSON Payload)</span>
              <span class="text-[10px] font-mono text-slate-400">Content-Type: application/json</span>
            </div>
            <textarea
              v-model="requestPayload"
              rows="5"
              class="input text-xs font-mono p-2.5 bg-slate-950 border border-cyber-line/80 text-cyan-300 rounded-md w-full focus:border-cyan-400 focus:ring-1 focus:ring-cyan-400/30 resize-y"
              placeholder="{}"
            ></textarea>
          </div>

          <!-- 调用操作按钮 -->
          <div class="flex justify-end">
            <button
              type="button"
              class="btn-primary py-2 px-5 text-xs flex items-center gap-2 bg-gradient-to-r from-cyan-600 to-blue-600 hover:from-cyan-500 hover:to-blue-500 text-white font-medium shadow-md disabled:opacity-50"
              :disabled="invoking || !selectedOperation"
              @click="handleInvoke()"
            >
              <span>{{ invoking ? '⏳' : '🚀' }}</span>
              <span>{{ invoking ? '网关调度调用中...' : '立即发送透明调用' }}</span>
            </button>
          </div>

          <!-- 调用结果展示区 -->
          <div v-if="invokeResult" class="panel p-3 border border-cyber-line/60 space-y-2.5">
            <div class="flex items-center justify-between text-xs border-b border-cyber-line/40 pb-2">
              <div class="flex items-center gap-2">
                <span
                  class="px-2 py-0.5 rounded text-[10px] font-mono font-semibold border"
                  :class="invokeResult.success ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/40' : 'bg-red-500/20 text-red-300 border-red-500/40'"
                >
                  {{ invokeResult.statusCode || (invokeResult.success ? 200 : 500) }} {{ invokeResult.success ? 'OK' : 'ERROR' }}
                </span>
                <span class="text-slate-300 font-medium">执行耗时:</span>
                <span class="text-amber-300 font-mono font-semibold">{{ (invokeResult.durationMs || invokeResult.latencyMs || 0).toFixed(2) }} ms</span>
              </div>
            </div>

            <!-- 输出内容展示 -->
            <div class="space-y-1">
              <div class="text-[11px] text-slate-400">响应载荷 (Response Payload):</div>
              <pre class="bg-slate-950 p-2.5 rounded text-xs font-mono text-emerald-400 overflow-x-auto max-h-48 border border-cyber-line/60 whitespace-pre-wrap break-all">{{ invokeResult.payload || invokeResult.output || '(空输出)' }}</pre>
            </div>

            <!-- 错误信息展示 -->
            <div v-if="invokeResult.errorMessage || invokeResult.error" class="space-y-1">
              <div class="text-[11px] text-red-400 font-medium">错误详情:</div>
              <pre class="bg-red-950/30 p-2 rounded text-xs font-mono text-red-300 border border-red-800/40 whitespace-pre-wrap break-all">{{ invokeResult.errorMessage || invokeResult.error }}</pre>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
