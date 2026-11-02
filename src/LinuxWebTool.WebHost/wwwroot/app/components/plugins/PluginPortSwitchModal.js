import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginPortSwitchModal',
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
  emits: ['close', 'switched'],
  setup(props, { emit }) {
    const targetPort = ref(18001);
    const restartIfRunning = ref(true);
    const checking = ref(false);
    const submitting = ref(false);
    const checkResult = ref(null);
    let checkDebounceTimer = null;

    watch(
      () => [props.show, props.plugin],
      ([show, p]) => {
        if (show && p) {
          const current = p.allocatedPort || p.manifest?.network?.port || 18001;
          targetPort.value = current;
          checkResult.value = null;
          triggerPortCheck(current);
        }
      },
      { immediate: true }
    );

    function onPortInput() {
      if (checkDebounceTimer) clearTimeout(checkDebounceTimer);
      checkDebounceTimer = setTimeout(() => {
        if (targetPort.value >= 1024 && targetPort.value <= 65535) {
          triggerPortCheck(targetPort.value);
        } else {
          checkResult.value = {
            isAvailable: false,
            message: '端口号必须在 1024 - 65535 范围内',
          };
        }
      }, 350);
    }

    async function triggerPortCheck(port) {
      if (!props.plugin) return;
      checking.value = true;
      try {
        const res = await http(API.plugins.checkPort(props.plugin.id, port), { method: 'GET' });
        if (res && res.data) {
          checkResult.value = res.data;
        } else {
          checkResult.value = { isAvailable: false, message: res?.message || '探测失败' };
        }
      } catch (err) {
        checkResult.value = { isAvailable: false, message: '端口探测异常: ' + err.message };
      } finally {
        checking.value = false;
      }
    }

    function applyRecommended() {
      if (checkResult.value?.recommendedPort) {
        targetPort.value = checkResult.value.recommendedPort;
        triggerPortCheck(targetPort.value);
      }
    }

    async function handleConfirm() {
      if (!props.plugin) return;
      if (targetPort.value < 1024 || targetPort.value > 65535) {
        toast.error('目标端口必须在 1024 到 65535 范围内');
        return;
      }

      submitting.value = true;
      try {
        const res = await http(API.plugins.switchPort(props.plugin.id), {
          method: 'POST',
          body: {
            newPort: Number(targetPort.value),
            restartIfRunning: Boolean(restartIfRunning.value),
          },
        });

        if (res.ok && res.data?.success) {
          toast.success(res.data.message || `端口已成功切换至 ${targetPort.value}`);
          emit('switched', { plugin: props.plugin, newPort: targetPort.value });
          emit('close');
        } else {
          toast.error(res.message || res.data?.message || '切换端口失败');
        }
      } catch (err) {
        toast.error('请求切换端口异常: ' + err.message);
      } finally {
        submitting.value = false;
      }
    }

    return {
      targetPort,
      restartIfRunning,
      checking,
      submitting,
      checkResult,
      onPortInput,
      applyRecommended,
      handleConfirm,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-md w-full p-5 border border-cyan-500/30 bg-slate-900/95 shadow-2xl animate-fade-in flex flex-col gap-4">
        <!-- 弹窗标题 -->
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">🔀</span>
            <div>
              <h3 class="font-display font-semibold text-slate-100 text-sm">快速切换微服务端口</h3>
              <p class="text-[11px] text-slate-400 font-mono">{{ plugin?.name }} ({{ plugin?.id }})</p>
            </div>
          </div>
          <button @click="$emit('close')" class="text-slate-400 hover:text-slate-200 text-lg leading-none">&times;</button>
        </div>

        <!-- 端口状态概览 -->
        <div class="bg-slate-800/60 p-3 rounded-lg border border-slate-700/50 flex flex-col gap-1.5 text-xs">
          <div class="flex justify-between items-center">
            <span class="text-slate-400">预设配置端口:</span>
            <span class="font-mono text-slate-200 font-medium">:{{ plugin?.manifest?.network?.port || '未定义' }}</span>
          </div>
          <div class="flex justify-between items-center">
            <span class="text-slate-400">当前运行端口:</span>
            <span class="font-mono" :class="plugin?.isPortAvoided ? 'text-amber-400 font-semibold' : 'text-cyan-300 font-medium'">
              {{ plugin?.allocatedPort ? ':' + plugin.allocatedPort : '未分配 (服务已停止)' }}
            </span>
          </div>
          <div v-if="plugin?.isPortAvoided" class="mt-1 p-2 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-[11px]">
            ⚠️ {{ plugin.portConflictReason || '声明目标端口被外部进程占用，系统已自动避让启动。' }}
          </div>
        </div>

        <!-- 目标端口输入与实时探活 -->
        <div class="flex flex-col gap-1.5">
          <label class="text-xs font-medium text-slate-300">目标端口 (1024 - 65535)</label>
          <div class="flex gap-2">
            <div class="relative flex-1">
              <span class="absolute left-3 top-1/2 -translate-y-1/2 font-mono text-slate-500 text-xs">:</span>
              <input
                v-model.number="targetPort"
                @input="onPortInput"
                type="number"
                min="1024"
                max="65535"
                class="w-full pl-6 pr-3 py-1.5 bg-slate-950 border border-cyber-line/60 rounded text-slate-100 font-mono text-sm focus:border-cyan-500 focus:outline-none"
                placeholder="例如 18005"
              />
            </div>
            <button
              v-if="checkResult?.recommendedPort && checkResult.recommendedPort !== targetPort"
              @click="applyRecommended()"
              type="button"
              class="px-2.5 py-1.5 bg-cyan-500/10 hover:bg-cyan-500/20 text-cyan-300 border border-cyan-500/30 rounded text-xs shrink-0 flex items-center gap-1 transition-colors"
              title="一键填入系统推荐的下一个完全空闲端口"
            >
              💡 推荐: {{ checkResult.recommendedPort }}
            </button>
          </div>

          <!-- 实时探针结果指示 -->
          <div class="text-[11px] min-h-[1.25rem] flex items-center gap-1.5">
            <span v-if="checking" class="text-slate-400 animate-pulse">正在检测端口可用性...</span>
            <template v-else-if="checkResult">
              <span v-if="checkResult.isAvailable" class="text-emerald-400 flex items-center gap-1">
                <span>✅</span> {{ checkResult.message || '端口空闲可用' }}
              </span>
              <span v-else class="text-rose-400 flex items-center gap-1">
                <span>❌</span> {{ checkResult.message || '端口不可用' }}
              </span>
            </template>
          </div>
        </div>

        <!-- 选项：自动重启 -->
        <div class="flex items-center gap-2 pt-1 border-t border-cyber-line/30">
          <input
            id="restartIfRunning"
            v-model="restartIfRunning"
            type="checkbox"
            class="rounded bg-slate-950 border-cyber-line/60 text-cyan-500 focus:ring-0 focus:ring-offset-0"
          />
          <label for="restartIfRunning" class="text-xs text-slate-300 cursor-pointer select-none">
            保存后自动重启微服务（平滑热重载并同步网关）
          </label>
        </div>

        <!-- 操作按钮 -->
        <div class="flex justify-end gap-2 pt-2 border-t border-cyber-line/50">
          <button
            @click="$emit('close')"
            type="button"
            class="px-3 py-1.5 rounded text-xs text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition-colors"
          >
            取消
          </button>
          <button
            @click="handleConfirm()"
            :disabled="submitting || (checkResult && !checkResult.isAvailable)"
            type="button"
            class="btn-primary text-xs px-4 py-1.5 flex items-center gap-1.5 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <span v-if="submitting" class="animate-spin text-xs">🌀</span>
            <span>{{ submitting ? '正在切换...' : '确认切换' }}</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
