import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'DockerExecModal',
  props: {
    container: { type: Object, default: null },
    visible: { type: Boolean, default: false },
  },
  emits: ['close'],
  setup(props, { emit }) {
    const command = ref('uname -a');
    const workingDir = ref('');
    const executing = ref(false);
    const lastResult = ref(null);

    const quickCommands = [
      { label: '系统信息', cmd: 'uname -a' },
      { label: '发行版', cmd: 'cat /etc/os-release' },
      { label: '环境变量', cmd: 'env' },
      { label: '进程列表', cmd: 'ps aux || ps -ef' },
      { label: '网卡配置', cmd: 'ip addr || ifconfig' },
      { label: '磁盘空间', cmd: 'df -h' },
      { label: '内存状态', cmd: 'free -m' },
    ];

    watch(() => props.visible, (v) => {
      if (v && !lastResult.value) {
        lastResult.value = null;
      }
    });

    async function execute() {
      if (!props.container?.id) return;
      const cmd = command.value.trim();
      if (!cmd) {
        toast.error('请输入待执行的命令');
        return;
      }

      executing.value = true;
      try {
        const res = await http(API.services.dockerContainerExec(props.container.id), {
          method: 'POST',
          body: {
            command: cmd,
            workingDir: workingDir.value.trim() || null,
            timeoutSeconds: 30,
          },
        });
        if (res.ok && res.data) {
          lastResult.value = res.data;
          if (res.data.exitCode === 0) {
            toast.success('命令执行完成');
          } else {
            toast.warning(`命令执行返回码：${res.data.exitCode}`);
          }
        } else {
          toast.error(res.message || '命令执行失败');
        }
      } catch (err) {
        toast.error('请求异常: ' + err.message);
      } finally {
        executing.value = false;
      }
    }

    function pickCommand(cmd) {
      command.value = cmd;
      execute();
    }

    function copyOutput() {
      if (!lastResult.value?.output && !lastResult.value?.error) return;
      const text = [lastResult.value.output, lastResult.value.error].filter(Boolean).join('\n');
      navigator.clipboard?.writeText(text);
      toast.success('已复制到剪贴板');
    }

    function close() {
      emit('close');
    }

    return {
      command,
      workingDir,
      executing,
      lastResult,
      quickCommands,
      execute,
      pickCommand,
      copyOutput,
      close,
    };
  },
  template: `
    <div v-if="visible" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
      <div class="panel max-w-4xl w-full max-h-[90vh] flex flex-col border-cyan-500/40 shadow-2xl">
        <!-- Header -->
        <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
          <div class="flex items-center gap-2">
            <span class="text-base">⚡</span>
            <span class="font-display font-semibold text-slate-100 text-sm">
              容器命令单次执行 ({{ container?.names?.[0] || container?.id?.slice(0, 12) }})
            </span>
            <span class="text-xs font-mono text-slate-400">[{{ container?.image }}]</span>
          </div>
          <button type="button" class="btn btn-xs" @click="close()">✕ 关闭</button>
        </div>

        <!-- Body -->
        <div class="p-4 flex-1 overflow-auto flex flex-col gap-3">
          <!-- Quick commands chips -->
          <div class="flex flex-wrap items-center gap-1.5">
            <span class="text-xs text-slate-400 mr-1">快捷命令:</span>
            <button
              v-for="qc in quickCommands"
              :key="qc.label"
              type="button"
              class="btn btn-xs bg-cyber-panel/60 hover:border-cyan-400 text-cyan-300"
              :disabled="executing"
              @click="pickCommand(qc.cmd)"
            >
              {{ qc.label }}
            </button>
          </div>

          <!-- Input bar -->
          <div class="flex items-center gap-2">
            <div class="flex-1 flex items-center gap-1.5">
              <input
                v-model="command"
                type="text"
                class="input text-xs font-mono w-full"
                placeholder="输入待执行命令，例如：uname -a 或 cat /etc/hosts"
                :disabled="executing"
                @keyup.enter="execute()"
              />
            </div>
            <input
              v-model="workingDir"
              type="text"
              class="input text-xs font-mono w-36"
              placeholder="工作目录 (默认 /)"
              :disabled="executing"
              @keyup.enter="execute()"
            />
            <button
              type="button"
              class="btn btn-xs btn-primary shrink-0 px-3 py-1 flex items-center gap-1"
              :disabled="executing || !command.trim()"
              @click="execute()"
            >
              <span v-if="executing" class="animate-spin text-xs">⏳</span>
              <span>{{ executing ? '执行中...' : '▶ 执行' }}</span>
            </button>
          </div>

          <!-- Result Area -->
          <div class="flex-1 flex flex-col border border-cyber-line/60 rounded bg-black/60 overflow-hidden min-h-[200px]">
            <div class="px-3 py-1.5 bg-cyber-panel/80 border-b border-cyber-line/40 flex items-center justify-between text-xs">
              <div class="flex items-center gap-2">
                <span class="text-slate-400 font-mono">执行结果</span>
                <span
                  v-if="lastResult"
                  class="badge text-[10px] px-1.5 py-0.2"
                  :class="lastResult.exitCode === 0 ? 'border-emerald-500/50 text-emerald-300 bg-emerald-500/10' : 'border-rose-500/50 text-rose-300 bg-rose-500/10'"
                >
                  退出码: {{ lastResult.exitCode }}
                </span>
                <span v-if="lastResult" class="text-slate-500 font-mono text-[10px]">
                  耗时: {{ lastResult.executionDurationMs }}ms
                </span>
              </div>
              <div class="flex items-center gap-1.5">
                <button
                  type="button"
                  class="btn btn-xs text-[11px]"
                  :disabled="!lastResult"
                  @click="copyOutput()"
                >
                  📋 复制
                </button>
                <button
                  type="button"
                  class="btn btn-xs text-[11px]"
                  :disabled="!lastResult"
                  @click="lastResult = null"
                >
                  清空
                </button>
              </div>
            </div>

            <div class="p-3 flex-1 overflow-auto">
              <pre v-if="lastResult?.output" class="font-mono text-xs text-slate-200 whitespace-pre-wrap leading-relaxed select-text">{{ lastResult.output }}</pre>
              <pre v-if="lastResult?.error" class="font-mono text-xs text-rose-300 whitespace-pre-wrap leading-relaxed select-text mt-2">{{ lastResult.error }}</pre>
              <div v-if="!lastResult && !executing" class="text-slate-500 text-xs text-center py-12">
                等待执行命令... 输入指令或点击快捷按钮立即执行
              </div>
              <div v-if="executing" class="text-cyan-400 text-xs text-center py-12 animate-pulse">
                命令正在容器中执行，请稍候...
              </div>
            </div>
          </div>
        </div>

        <!-- Footer -->
        <div class="p-3 border-t border-cyber-line/50 flex justify-end">
          <button type="button" class="btn btn-xs" @click="close()">关闭</button>
        </div>
      </div>
    </div>
  `,
});
