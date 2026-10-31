import { defineComponent, ref } from 'vue';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'NativeWorkerDeployModal',
  props: {
    show: { type: Boolean, default: false },
  },
  emits: ['close', 'submit'],
  setup(props, { emit }) {
    const id = ref('');
    const name = ref('');
    const description = ref('');
    const source = ref('package'); // package | system
    const programRef = ref('');
    const argsText = ref('');
    const enableControl = ref(false);
    const controlPort = ref(18888);
    const controlToken = ref('');
    const controlHealthPath = ref('/control/health');
    const controlShutdownPath = ref('/control/shutdown');

    function closeModal() {
      emit('close');
    }

    function generateRandomToken() {
      const arr = new Uint8Array(16);
      crypto.getRandomValues(arr);
      controlToken.value = Array.from(arr, (b) => b.toString(16).padStart(2, '0')).join('');
    }

    function handleSubmit() {
      if (!id.value.trim()) {
        toast.warning('请输入原生服务标识 (ID)');
        return;
      }
      if (!name.value.trim()) {
        toast.warning('请输入原生服务名称');
        return;
      }
      if (!programRef.value.trim()) {
        toast.warning('请输入程序相对路径或系统程序名');
        return;
      }

      const args = argsText.value
        .split('\n')
        .map((s) => s.trim())
        .filter((s) => s.length > 0);

      const payload = {
        id: id.value.trim(),
        name: name.value.trim(),
        description: description.value.trim(),
        runtime: 6, // Native = 6
        source: source.value,
        programRef: programRef.value.trim(),
        arguments: args,
      };

      if (enableControl.value) {
        payload.control = {
          port: Number(controlPort.value) || 18888,
          token: controlToken.value.trim() || undefined,
          healthPath: controlHealthPath.value.trim() || '/control/health',
          shutdownPath: controlShutdownPath.value.trim() || '/control/shutdown',
        };
      }

      emit('submit', payload);
      closeModal();
    }

    return {
      id,
      name,
      description,
      source,
      programRef,
      argsText,
      enableControl,
      controlPort,
      controlToken,
      controlHealthPath,
      controlShutdownPath,
      closeModal,
      generateRandomToken,
      handleSubmit,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-xl w-full p-5 flex flex-col gap-4 border-cyan-500/40 animate-fade-in max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">⚙️</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">部署 Native 原生常驻服务</div>
              <div class="text-[11px] text-slate-400 mt-0.5">直接宿主托管原生二进制或受信系统工具</div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <div class="space-y-3 text-xs">
          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="text-slate-300 font-medium block mb-1">服务 ID (标识符):</label>
              <input v-model="id" type="text" class="input w-full font-mono" placeholder="my-native-tool" />
            </div>
            <div>
              <label class="text-slate-300 font-medium block mb-1">服务名称:</label>
              <input v-model="name" type="text" class="input w-full" placeholder="我的原生守护服务" />
            </div>
          </div>

          <div>
            <label class="text-slate-300 font-medium block mb-1">描述信息:</label>
            <input v-model="description" type="text" class="input w-full" placeholder="常驻后台服务功能说明" />
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="text-slate-300 font-medium block mb-1">程序来源 (Source):</label>
              <select v-model="source" class="select w-full font-mono">
                <option value="package">📦 插件安装包内部 (package)</option>
                <option value="system">🖥️ 系统受信任程序 (system)</option>
              </select>
            </div>
            <div>
              <label class="text-slate-300 font-medium block mb-1">程序引用 (ProgramRef):</label>
              <input v-model="programRef" type="text" class="input w-full font-mono" placeholder="bin/service 或 ffmpeg" />
            </div>
          </div>

          <div>
            <label class="text-slate-300 font-medium block mb-1">启动参数列表 (每行一个参数，防 Shell 注入):</label>
            <textarea
              v-model="argsText"
              rows="3"
              class="input w-full font-mono leading-relaxed"
              placeholder="--mode=resident&#10;--verbose&#10;--data-dir=data"
            ></textarea>
          </div>

          <!-- HTTP 管理控制配置 -->
          <div class="p-3 rounded bg-slate-900/60 border border-cyber-line/50 space-y-2">
            <div class="flex items-center justify-between">
              <label class="flex items-center gap-2 cursor-pointer font-medium text-slate-200">
                <input type="checkbox" v-model="enableControl" class="accent-cyan-400" />
                <span>启用 HTTP 回环管理控制协议 (可选)</span>
              </label>
              <span class="text-[10px] text-cyan-400 font-mono">Loopback 127.0.0.1</span>
            </div>

            <div v-if="enableControl" class="space-y-2.5 pt-2 border-t border-cyber-line/30">
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="text-slate-400 block mb-1">控制端口 (Port):</label>
                  <input v-model.number="controlPort" type="number" class="input w-full font-mono" />
                </div>
                <div>
                  <div class="flex items-center justify-between mb-1">
                    <label class="text-slate-400">控制令牌 (Token):</label>
                    <button type="button" class="text-[10px] text-cyan-400 hover:underline" @click="generateRandomToken()">随机生成</button>
                  </div>
                  <input v-model="controlToken" type="text" class="input w-full font-mono" placeholder="留空自动生成" />
                </div>
              </div>

              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="text-slate-400 block mb-1">健康检查端点:</label>
                  <input v-model="controlHealthPath" type="text" class="input w-full font-mono" />
                </div>
                <div>
                  <label class="text-slate-400 block mb-1">优雅停止端点:</label>
                  <input v-model="controlShutdownPath" type="text" class="input w-full font-mono" />
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="flex items-center justify-end gap-2 pt-2 border-t border-cyber-line/40">
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">取消</button>
          <button type="button" class="btn btn-xs btn-primary min-w-[80px]" @click="handleSubmit()">确认部署</button>
        </div>
      </div>
    </div>
  `,
});
