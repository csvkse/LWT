import { defineComponent, ref, watch } from 'vue';
import { API } from '../../config.js';
import { httpGet, httpPost } from '../../api/client.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'WorkerCodeModal',
  props: {
    show: { type: Boolean, default: false },
    worker: { type: Object, default: () => ({}) },
  },
  emits: ['close', 'saved'],
  setup(props, { emit }) {
    const loading = ref(false);
    const saving = ref(false);
    const debugging = ref(false);

    const availableFiles = ref([]);
    const selectedFile = ref('');
    const codeContent = ref('');
    const backups = ref([]);
    const restartAfterSave = ref(true);

    const debugArgs = ref('');
    const debugResult = ref(null);

    async function loadCode(file = null) {
      if (!props.worker?.id) return;
      loading.value = true;
      try {
        const url = API.workers.code(props.worker.id, file || selectedFile.value);
        const res = await httpGet(url);
        if (res.ok && res.data) {
          selectedFile.value = res.data.selectedFile || '';
          codeContent.value = res.data.content || '';
          availableFiles.value = res.data.availableFiles || [];
          backups.value = res.data.backups || [];
        } else {
          toast.error(res.message || '加载 Worker 代码失败');
        }
      } catch (err) {
        toast.error('请求异常: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    watch(() => props.show, (val) => {
      if (val && props.worker?.id) {
        debugResult.value = null;
        loadCode();
      }
    });

    function onFileSelect(e) {
      const file = e.target.value;
      if (file) {
        selectedFile.value = file;
        loadCode(file);
      }
    }

    async function saveCode() {
      if (!props.worker?.id || !selectedFile.value) return;
      saving.value = true;
      try {
        const url = API.workers.code(props.worker.id);
        const res = await httpPost(url, {
          filePath: selectedFile.value,
          content: codeContent.value,
          restartAfterSave: restartAfterSave.value,
        });
        if (res.ok) {
          toast.success(`Worker 代码保存成功${restartAfterSave.value ? '，并已请求热重载' : ''}`);
          emit('saved');
          await loadCode(selectedFile.value);
        } else {
          toast.error(res.message || '保存失败');
        }
      } catch (err) {
        toast.error('保存异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    async function runDebug() {
      if (!props.worker?.id) return;
      debugging.value = true;
      debugResult.value = null;
      try {
        toast.info(`正在瞬态沙箱调试执行 Worker [${props.worker.id}]...`);
        const url = API.workers.debug(props.worker.id);
        const res = await httpPost(url, {
          args: debugArgs.value || null,
          timeoutSeconds: 8,
        });
        if (res.ok && res.data) {
          debugResult.value = res.data;
          if (res.data.success) {
            toast.success(`调试执行成功 (耗时 ${res.data.elapsedMilliseconds} ms)`);
          } else {
            toast.error(`调试执行返回错误 (退出码: ${res.data.exitCode})`);
          }
        } else {
          toast.error(res.message || '调试执行失败');
        }
      } catch (err) {
        toast.error('调试调用异常: ' + err.message);
      } finally {
        debugging.value = false;
      }
    }

    function closeModal() {
      emit('close');
    }

    return {
      loading,
      saving,
      debugging,
      availableFiles,
      selectedFile,
      codeContent,
      backups,
      restartAfterSave,
      debugArgs,
      debugResult,
      loadCode,
      onFileSelect,
      saveCode,
      runDebug,
      closeModal,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-black/80 backdrop-blur-sm">
      <div class="panel max-w-4xl w-full p-4 flex flex-col gap-3 border-cyber-line/60 animate-fade-in shadow-2xl h-[88vh] max-h-[850px]">
        <!-- 头部导航与状态 -->
        <div class="flex items-center justify-between border-b border-cyber-line/40 pb-2.5">
          <div class="flex items-center gap-2">
            <span class="text-xl">🧑‍💻</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm flex items-center gap-2">
                <span>函数微服务代码与配置编辑</span>
                <span class="badge badge-outline text-[11px] font-mono text-cyber-neon border-cyber-neon/40">{{ worker?.name }} ({{ worker?.id }})</span>
              </div>
              <div class="text-[11px] text-cyber-muted mt-0.5">支持在线修改脚本与 worker.json，自动生成备忘录快照，支持瞬态调试执行</div>
            </div>
          </div>
          <button @click="closeModal()" class="text-slate-400 hover:text-slate-200 transition-colors">✕</button>
        </div>

        <!-- 工具栏：文件选择器 + 调试命令行 -->
        <div class="flex flex-wrap items-center justify-between gap-2 bg-cyber-bg/60 p-2 rounded border border-cyber-line/30 text-xs">
          <div class="flex items-center gap-2">
            <span class="text-slate-400 font-medium">文件:</span>
            <select
              :value="selectedFile"
              @change="onFileSelect($event)"
              class="input py-1 px-2 text-xs font-mono bg-cyber-bg border-cyber-line/50 rounded"
              :disabled="loading"
            >
              <option v-for="f in availableFiles" :key="f" :value="f">{{ f }}</option>
            </select>
            <button class="btn btn-ghost py-1 px-2 text-[11px]" @click="loadCode(selectedFile)" :disabled="loading" title="重新载入">
              🔄 刷新
            </button>
            <span v-if="backups.length" class="text-[10px] text-slate-400 font-mono ml-1">
              (快照: {{ backups.length }} 份)
            </span>
          </div>

          <div class="flex items-center gap-2">
            <input
              v-model="debugArgs"
              type="text"
              placeholder="测试参数 (可选)"
              class="input py-1 px-2 text-xs font-mono w-40 bg-cyber-bg border-cyber-line/50 rounded"
            />
            <button
              class="btn btn-secondary py-1 px-3 text-xs flex items-center gap-1"
              :disabled="debugging || loading"
              @click="runDebug()"
            >
              <span v-if="debugging" class="animate-spin text-xs">⏳</span>
              <span>{{ debugging ? '调试中...' : '⚡ 瞬态调试' }}</span>
            </button>
          </div>
        </div>

        <!-- 主内容区：代码编辑与调试输出 -->
        <div class="flex-1 min-h-0 flex flex-col gap-2">
          <div class="flex-1 relative flex flex-col border border-cyber-line/40 rounded overflow-hidden bg-[#0d1117]">
            <div v-if="loading" class="absolute inset-0 bg-black/60 backdrop-blur-xs flex items-center justify-center z-10 text-xs text-cyber-primary">
              <span class="animate-spin mr-2">⏳</span> 正在读取 Worker 脚本...
            </div>
            <textarea
              v-model="codeContent"
              class="w-full h-full p-3 font-mono text-xs bg-transparent text-slate-200 border-none outline-none resize-none leading-relaxed"
              placeholder="// 在此处输入代码..."
              spellcheck="false"
            ></textarea>
          </div>

          <!-- 调试输出面板 -->
          <div v-if="debugResult" class="h-36 shrink-0 bg-black/90 border border-cyber-line/50 rounded p-2.5 flex flex-col gap-1 text-xs font-mono overflow-y-auto">
            <div class="flex items-center justify-between text-[11px] pb-1 border-b border-cyber-line/30">
              <span class="font-bold flex items-center gap-1.5" :class="debugResult.success ? 'text-emerald-400' : 'text-rose-400'">
                <span>{{ debugResult.success ? '✅ 执行成功' : '❌ 执行失败' }}</span>
                <span class="text-slate-400 font-normal">(Exit Code: {{ debugResult.exitCode }}, {{ debugResult.elapsedMilliseconds }} ms)</span>
              </span>
              <button class="text-slate-400 hover:text-slate-200 text-[10px]" @click="debugResult = null">关闭输出</button>
            </div>
            <div v-if="debugResult.standardOutput" class="text-emerald-300/90 whitespace-pre-wrap">{{ debugResult.standardOutput }}</div>
            <div v-if="debugResult.standardError" class="text-rose-400/90 whitespace-pre-wrap">{{ debugResult.standardError }}</div>
            <div v-if="!debugResult.standardOutput && !debugResult.standardError" class="text-slate-500 italic">没有标准输出返回</div>
          </div>
        </div>

        <!-- 底部保存与热重启选项 -->
        <div class="flex items-center justify-between border-t border-cyber-line/40 pt-2.5">
          <label class="flex items-center gap-2 cursor-pointer text-xs text-slate-300">
            <input type="checkbox" v-model="restartAfterSave" class="checkbox checkbox-sm checkbox-primary" />
            <span>保存后自动热重载 Worker 进程</span>
          </label>
          <div class="flex items-center gap-2">
            <button type="button" class="btn btn-ghost text-xs" :disabled="saving" @click="closeModal()">
              关闭
            </button>
            <button
              type="button"
              class="btn btn-primary text-xs flex items-center gap-1.5"
              :disabled="saving || loading || !selectedFile"
              @click="saveCode()"
            >
              <span v-if="saving" class="animate-spin text-xs">⏳</span>
              <span>{{ saving ? '正在保存...' : '💾 保存代码' }}</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
