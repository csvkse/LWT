import { computed, defineComponent, ref, watch } from 'vue';
import { http, httpDownload, httpUpload } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';
import PluginBinaryFileCard from './PluginBinaryFileCard.js';
import PluginFileTree from './PluginFileTree.js';

export default defineComponent({
  name: 'PluginCodeModal',
  components: {
    PluginBinaryFileCard,
    PluginFileTree,
  },
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
  emits: ['close', 'saved'],
  setup(props, { emit }) {
    const loading = ref(false);
    const saving = ref(false);
    const debugging = ref(false);
    const uploading = ref(false);
    const deleting = ref(false);
    const fileList = ref([]);
    const activeFile = ref('');
    const codeContent = ref('');
    const isBinary = ref(false);
    const fileSizeBytes = ref(0);
    const debugInput = ref('');
    const debugResult = ref(null);
    const replaceInputRef = ref(null);
    const newFileInputRef = ref(null);

    const isProtectedFile = computed(() => activeFile.value?.toLowerCase() === 'plugin.json');

    function formatSize(bytes) {
      if (!bytes || bytes <= 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return (bytes / Math.pow(k, i)).toFixed(i === 0 ? 0 : 1) + ' ' + sizes[i];
    }

    watch(
      () => [props.show, props.plugin],
      async ([show, p]) => {
        if (show && p) {
          debugResult.value = null;
          await loadCode();
        }
      },
      { immediate: true }
    );

    async function loadCode(targetFile = null) {
      if (!props.plugin) return;
      loading.value = true;
      try {
        const res = await http(API.plugins.code(props.plugin.id, targetFile), { method: 'GET' });
        if (res.ok && res.data) {
          fileList.value = res.data.files || [];
          activeFile.value = res.data.activeFile || '';
          codeContent.value = res.data.content || '';
          isBinary.value = Boolean(res.data.isBinary);
          fileSizeBytes.value = res.data.fileSizeBytes || 0;
        } else {
          toast.error(res.message || '加载代码文件失败');
        }
      } catch (err) {
        toast.error('请求代码失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function handleSave() {
      if (!props.plugin || !activeFile.value || isBinary.value) return;
      saving.value = true;
      try {
        const res = await http(API.plugins.code(props.plugin.id), {
          method: 'POST',
          body: {
            file: activeFile.value,
            content: codeContent.value,
          },
        });
        if (res.ok) {
          toast.success(`保存成功（已创建 .bak 快照）`);
          emit('saved');
        } else {
          toast.error(res.message || '保存代码失败');
        }
      } catch (err) {
        toast.error('保存代码异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    async function handleDebug() {
      if (!props.plugin || !activeFile.value || isBinary.value) return;
      debugging.value = true;
      debugResult.value = null;
      try {
        const res = await http(API.plugins.debug(props.plugin.id), {
          method: 'POST',
          body: {
            file: activeFile.value,
            input: debugInput.value,
          },
        });
        if (res.ok && res.data) {
          debugResult.value = res.data;
          toast.info(`调试执行完成，退出码: ${res.data.exitCode}`);
        } else {
          toast.error(res.message || '调试执行失败');
        }
      } catch (err) {
        toast.error('调试运行异常: ' + err.message);
      } finally {
        debugging.value = false;
      }
    }

    async function handleDownloadFile() {
      if (!props.plugin || !activeFile.value) return;
      const res = await httpDownload(API.plugins.fileDownload(props.plugin.id, activeFile.value));
      if (res.ok) {
        toast.success(`文件已下载: ${res.filename || activeFile.value}`);
      }
    }

    async function handleFileSelected(event, isReplace = false) {
      const file = event.target?.files?.[0];
      if (!file || !props.plugin) return;
      event.target.value = '';

      const targetPath = isReplace && activeFile.value ? activeFile.value : file.name;
      uploading.value = true;
      try {
        const res = await httpUpload(API.plugins.fileUpload(props.plugin.id, targetPath), { file });
        if (res.ok) {
          toast.success(`文件上传成功: ${targetPath}`);
          await loadCode(targetPath);
          emit('saved');
        } else {
          toast.error(res.message || '文件上传失败');
        }
      } catch (err) {
        toast.error('上传异常: ' + err.message);
      } finally {
        uploading.value = false;
      }
    }

    async function handleDeleteFile() {
      if (!props.plugin || !activeFile.value) return;
      if (isProtectedFile.value) {
        toast.warning('plugin.json 为核心插件描述清单，受系统保护不可删除');
        return;
      }
      if (!confirm(`确定要永久删除文件 "${activeFile.value}" 吗？此操作无法撤销。`)) {
        return;
      }
      deleting.value = true;
      try {
        const res = await http(API.plugins.fileDelete(props.plugin.id, activeFile.value), {
          method: 'DELETE',
        });
        if (res.ok) {
          toast.success(`文件已删除: ${activeFile.value}`);
          await loadCode();
          emit('saved');
        } else {
          toast.error(res.message || '删除文件失败');
        }
      } catch (err) {
        toast.error('删除异常: ' + err.message);
      } finally {
        deleting.value = false;
      }
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

    const cleanDebugOutput = computed(() => stripAnsi(debugResult.value?.output || ''));
    const cleanDebugError = computed(() => stripAnsi(debugResult.value?.error || ''));

    return {
      loading,
      saving,
      debugging,
      uploading,
      deleting,
      fileList,
      activeFile,
      codeContent,
      isBinary,
      fileSizeBytes,
      isProtectedFile,
      formatSize,
      debugInput,
      debugResult,
      cleanDebugOutput,
      cleanDebugError,
      replaceInputRef,
      newFileInputRef,
      loadCode,
      handleSave,
      handleDebug,
      handleDownloadFile,
      handleFileSelected,
      handleDeleteFile,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-6xl h-[90vh] p-0 flex flex-col border border-cyan-500/40 overflow-hidden shadow-2xl">
        <input ref="replaceInputRef" type="file" class="hidden" @change="e => handleFileSelected(e, true)" />
        <input ref="newFileInputRef" type="file" class="hidden" @change="e => handleFileSelected(e, false)" />

        <!-- 头部 -->
        <div class="px-4 py-2.5 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between shrink-0">
          <div class="flex items-center gap-2 min-w-0">
            <span class="text-lg">✏️</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm truncate">
                文件动态管理与代码编辑器 - {{ plugin.name }} ({{ plugin.id }})
              </div>
              <div class="text-[11px] text-slate-400">
                支持源码编辑、二进制免重建容器热更、文件上传/下载/替换与沙箱调试
              </div>
            </div>
          </div>

          <div class="flex items-center gap-2 shrink-0">
            <button
              v-if="!isBinary"
              type="button"
              class="btn-secondary text-xs px-3 py-1 flex items-center gap-1.5"
              :disabled="saving || loading || debugging || uploading || !activeFile"
              @click="handleDebug()"
            >
              <span>🐞</span>
              <span>{{ debugging ? '运行中...' : '试运行调试' }}</span>
            </button>
            <button
              v-if="!isBinary"
              type="button"
              class="btn-primary text-xs px-3 py-1 flex items-center gap-1.5"
              :disabled="saving || loading || uploading || !activeFile"
              @click="handleSave()"
            >
              <span>💾</span>
              <span>{{ saving ? '保存中...' : '保存修改' }}</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white shrink-0"
              @click="$emit('close')"
            >
              ✕
            </button>
          </div>
        </div>

        <!-- 主体：左侧文件列表 + 右侧编辑/管理区 -->
        <div class="flex-1 flex min-h-0 overflow-hidden">
          <!-- 左侧文件树组件 -->
          <PluginFileTree
            :file-list="fileList"
            :active-file="activeFile"
            :uploading="uploading"
            @select="file => loadCode(file)"
            @upload="newFileInputRef?.click()"
          />

          <!-- 右侧编辑器 / 二进制卡片与操作区 -->
          <div class="flex-1 flex flex-col min-w-0 bg-slate-900/60">
            <div class="px-4 py-1.5 bg-slate-950/70 border-b border-cyber-line/40 text-xs font-mono text-cyan-300 flex items-center justify-between shrink-0">
              <div class="flex items-center gap-2 min-w-0 truncate">
                <span class="font-semibold truncate">{{ activeFile || '未选择文件' }}</span>
                <span v-if="fileSizeBytes" class="text-slate-400 text-[11px] font-sans">
                  ({{ formatSize(fileSizeBytes) }})
                </span>
                <span v-if="isBinary" class="px-1.5 py-0.2 rounded text-[10px] bg-purple-500/20 text-purple-300 border border-purple-500/30">
                  二进制/库
                </span>
                <span v-if="loading" class="text-slate-500 text-[11px] animate-pulse">加载中...</span>
              </div>

              <div v-if="activeFile" class="flex items-center gap-1.5 shrink-0 ml-2">
                <button
                  type="button"
                  class="btn-ghost text-xs px-2 py-0.5 text-slate-300 hover:text-cyan-300 flex items-center gap-1"
                  title="下载当前文件到本地"
                  @click="handleDownloadFile()"
                >
                  <span>⬇️</span>
                  <span class="hidden sm:inline">下载</span>
                </button>
                <button
                  type="button"
                  class="btn-ghost text-xs px-2 py-0.5 text-slate-300 hover:text-amber-300 flex items-center gap-1"
                  title="选择本地新文件上传覆盖替换此文件"
                  :disabled="uploading"
                  @click="replaceInputRef?.click()"
                >
                  <span>⬆️</span>
                  <span class="hidden sm:inline">{{ uploading ? '上传中...' : '替换' }}</span>
                </button>
                <button
                  v-if="!isProtectedFile"
                  type="button"
                  class="btn-ghost text-xs px-2 py-0.5 text-slate-400 hover:text-red-400 flex items-center gap-1"
                  title="永久删除当前文件"
                  :disabled="deleting"
                  @click="handleDeleteFile()"
                >
                  <span>🗑️</span>
                  <span class="hidden sm:inline">{{ deleting ? '删除中...' : '删除' }}</span>
                </button>
              </div>
            </div>

            <!-- 二进制文件专属卡片面板 (Native / ELF / so / dll / zip 等免重建容器热更) -->
            <PluginBinaryFileCard
              v-if="isBinary"
              :active-file="activeFile"
              :file-size-bytes="fileSizeBytes"
              :uploading="uploading"
              @download="handleDownloadFile()"
              @replace="replaceInputRef?.click()"
            />

            <!-- 文本代码编辑文本框 (非二进制文件) -->
            <div v-else class="flex-1 min-h-0 p-3">
              <textarea
                v-model="codeContent"
                class="w-full h-full p-3 font-mono text-xs bg-slate-950 text-slate-200 border border-cyber-line/60 rounded focus:outline-none focus:border-cyan-500/60 resize-none leading-relaxed"
                spellcheck="false"
                :disabled="loading"
              ></textarea>
            </div>

            <!-- 调试输出控制台 (若有调试结果或调试中) -->
            <div v-if="!isBinary && (debugResult || debugging)" class="h-44 border-t border-cyber-line/60 bg-slate-950 flex flex-col shrink-0">
              <div class="px-3 py-1 bg-slate-900 border-b border-cyber-line/40 text-xs flex items-center justify-between text-slate-300">
                <span class="font-semibold flex items-center gap-1.5">
                  <span>📟 调试控制台输出</span>
                  <span v-if="debugResult" class="text-[11px] font-mono text-slate-400">
                    退出码: {{ debugResult.exitCode }} | 耗时: {{ debugResult.durationMs }}ms
                  </span>
                </span>
                <button type="button" class="btn-ghost text-[11px] p-0.5 text-slate-400 hover:text-slate-200" @click="debugResult = null">
                  清除
                </button>
              </div>
              <div class="flex-1 p-2.5 overflow-y-auto font-mono text-xs space-y-1">
                <div v-if="debugging" class="text-amber-400 animate-pulse">调试运行中...</div>
                <div v-if="cleanDebugOutput" class="text-slate-300 whitespace-pre-wrap">{{ cleanDebugOutput }}</div>
                <div v-if="cleanDebugError" class="text-red-400 whitespace-pre-wrap">{{ cleanDebugError }}</div>
                <div v-if="!debugging && !cleanDebugOutput && !cleanDebugError" class="text-slate-500 italic">
                  （无标准输出或错误信息）
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
