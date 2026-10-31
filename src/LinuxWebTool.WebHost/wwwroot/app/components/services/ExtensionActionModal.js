import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionActionModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: () => ({}) },
    actionDef: { type: Object, default: () => ({}) },
    env: { type: Object, default: () => ({}) },
  },
  emits: ['close', 'refresh'],
  setup(props, { emit }) {
    const inputParam = ref('');
    const executing = ref(false);
    const actionResult = ref(null);
    const selectedTarget = ref('container');

    watch(() => props.show, (val) => {
      if (val) {
        selectedTarget.value = props.ext?.target || props.ext?.manifest?.target || (props.ext?.manifest?.id === 'ssh-access-guard' ? 'host' : 'container');
      }
    }, { immediate: true });

    function closeModal() {
      inputParam.value = '';
      actionResult.value = null;
      emit('close');
    }

    async function submitAction() {
      if (props.actionDef?.requireParam && !inputParam.value.trim()) {
        toast.warning(`请输入${props.actionDef.paramName || '参数'}`);
        return;
      }

      executing.value = true;
      actionResult.value = null;

      try {
        const body = {
          extensionId: props.ext?.manifest?.id,
          action: props.actionDef?.action,
          target: selectedTarget.value,
        };
        if (props.actionDef?.requireParam) {
          body.actionParams = {
            [props.actionDef.paramName || 'ip']: inputParam.value.trim(),
          };
        }

        const res = await http(API.services.extensionAction, {
          method: 'POST',
          body,
        });

        if (res.ok && res.data) {
          actionResult.value = res.data;
          if (res.data.success) {
            toast.success(res.data.message || '操作成功完成');
            emit('refresh');
          } else {
            toast.error(res.data.message || '操作失败');
          }
        } else {
          toast.error(res.message || '请求处理失败');
        }
      } catch (err) {
        toast.error('请求异常: ' + err.message);
      } finally {
        executing.value = false;
      }
    }

    return {
      inputParam,
      executing,
      actionResult,
      selectedTarget,
      closeModal,
      submitAction,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-2xl w-full p-5 flex flex-col gap-4 border-cyan-500/40 animate-fade-in max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">{{ actionDef?.icon || '⚙️' }}</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">
                {{ actionDef?.label || actionDef?.action }}
              </div>
              <div class="text-[11px] text-slate-400 mt-0.5">
                扩展服务: <span class="font-mono text-cyan-400">{{ ext?.manifest?.name }}</span>
              </div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <div v-if="actionDef?.description" class="text-xs text-slate-300">
          {{ actionDef.description }}
        </div>

        <!-- 执行目标切换 (特权容器下可见) -->
        <div v-if="env?.isPrivilegedContainer" class="p-2.5 rounded bg-slate-900/60 border border-cyber-line/50 text-xs flex flex-col gap-1.5">
          <div class="flex items-center justify-between">
            <span class="text-slate-400 font-medium">执行目标 (Target):</span>
            <span class="text-[10px] text-cyan-400 font-mono">特权模式透明穿透</span>
          </div>
          <div class="flex items-center gap-4">
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
              <input type="radio" v-model="selectedTarget" value="container" class="accent-cyan-400" />
              <span>📦 当前容器</span>
            </label>
            <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
              <input type="radio" v-model="selectedTarget" value="host" class="accent-cyan-400" />
              <span>🖥️ 宿主主机 (nsenter穿透)</span>
            </label>
          </div>
        </div>

        <!-- 参数输入项 -->
        <div v-if="actionDef?.requireParam" class="space-y-1.5">
          <label class="text-xs text-slate-300 font-medium">
            {{ actionDef.paramName ? actionDef.paramName.toUpperCase() : '目标参数' }}:
          </label>
          <input
            v-model="inputParam"
            type="text"
            class="input text-xs w-full font-mono"
            :placeholder="actionDef.paramPlaceholder || '请输入参数...'"
            @keyup.enter="submitAction()"
          />
        </div>

        <!-- 执行按钮 -->
        <div class="flex items-center justify-end gap-2 pt-2">
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">
            关闭
          </button>
          <button
            type="button"
            class="btn btn-xs btn-primary min-w-[80px]"
            :disabled="executing"
            @click="submitAction()"
          >
            {{ executing ? '正在执行...' : '执行动作' }}
          </button>
        </div>

        <!-- 输出结果展示 -->
        <div v-if="actionResult" class="space-y-2 pt-2 border-t border-cyber-line/40">
          <div class="flex items-center justify-between text-xs">
            <span class="font-semibold" :class="actionResult.success ? 'text-emerald-400' : 'text-rose-400'">
              {{ actionResult.success ? '✅ 执行输出:' : '❌ 执行异常:' }}
            </span>
            <span class="text-[11px] text-slate-400 font-mono">{{ actionResult.message }}</span>
          </div>
          <pre
            v-if="actionResult.output"
            class="p-3 bg-cyber-bg/95 rounded border border-cyber-line/70 font-mono text-xs text-slate-300 max-h-64 overflow-y-auto whitespace-pre-wrap select-all leading-relaxed"
          >{{ actionResult.output }}</pre>
        </div>
      </div>
    </div>
  `,
});
