import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';
import { useFileTree } from '../../utils/fileTree.js';

export default defineComponent({
  name: 'PluginCodeModal',
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
    const fileList = ref([]);
    const activeFile = ref('');
    const codeContent = ref('');
    const debugInput = ref('');
    const debugResult = ref(null);

    const { visibleNodes, toggleFolder, expandAll, collapseAll } = useFileTree(fileList, activeFile);

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
        } else {
          toast.error(res.message || '加载代码文件失败');
        }
      } catch (err) {
        toast.error('请求代码失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function switchFile(file) {
      if (file === activeFile.value) return;
      await loadCode(file);
    }

    async function handleSave() {
      if (!props.plugin || !activeFile.value) return;
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
      if (!props.plugin || !activeFile.value) return;
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

    async function onNodeClick(node) {
      if (!node) return;
      if (node.isDir) {
        toggleFolder(node.fullPath);
      } else {
        await switchFile(node.fullPath);
      }
    }

    function clearDebugResult() {
      debugResult.value = null;
    }

    function handleClose() {
      emit('close');
    }

    return {
      loading,
      saving,
      debugging,
      fileList,
      activeFile,
      codeContent,
      debugInput,
      debugResult,
      visibleNodes,
      onNodeClick,
      expandAll,
      collapseAll,
      clearDebugResult,
      switchFile,
      handleSave,
      handleDebug,
      handleClose,
    };
  },
  template: `
    <div v-if="show && plugin" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-6xl h-[90vh] p-0 flex flex-col border border-cyan-500/40 overflow-hidden shadow-2xl">
        <!-- 头部 -->
        <div class="px-4 py-2.5 bg-slate-900 border-b border-cyber-line/60 flex items-center justify-between shrink-0">
          <div class="flex items-center gap-2 min-w-0">
            <span class="text-lg">✏️</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">
                代码编辑器与调试 - {{ plugin.name }} ({{ plugin.id }})
              </div>
              <div class="text-[11px] text-slate-400">
                支持编辑代码、实时保存（自动生成 .bak 备份快照）与沙箱调试运行
              </div>
            </div>
          </div>

          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn-secondary text-xs px-3 py-1 flex items-center gap-1.5"
              :disabled="saving || loading || debugging"
              @click="handleDebug()"
            >
              <span>🐞</span>
              <span>{{ debugging ? '运行中...' : '试运行调试' }}</span>
            </button>
            <button
              type="button"
              class="btn-primary text-xs px-3 py-1 flex items-center gap-1.5"
              :disabled="saving || loading"
              @click="handleSave()"
            >
              <span>💾</span>
              <span>{{ saving ? '保存中...' : '保存修改' }}</span>
            </button>
            <button
              type="button"
              class="btn-ghost text-xs p-1 text-slate-400 hover:text-white"
              @click="handleClose()"
            >
              ✕
            </button>
          </div>
        </div>

        <!-- 主体：左侧文件列表 + 右侧编辑/调试区 -->
        <div class="flex-1 flex min-h-0 overflow-hidden">
          <!-- 左侧文件树 -->
          <div class="w-60 shrink-0 bg-slate-950 border-r border-cyber-line/60 flex flex-col overflow-hidden">
            <!-- 头部操作栏 -->
            <div class="px-2.5 py-1.5 bg-slate-900/80 border-b border-cyber-line/40 flex items-center justify-between shrink-0">
              <span class="text-[11px] font-semibold text-slate-300 uppercase tracking-wider flex items-center gap-1">
                <span>📁</span>
                <span>插件文件 ({{ fileList.length }})</span>
              </span>
              <div class="flex items-center gap-1">
                <button
                  type="button"
                  class="btn-ghost text-[10px] px-1.5 py-0.5 text-slate-400 hover:text-cyan-300"
                  title="展开全部目录"
                  @click="expandAll()"
                >
                  展开
                </button>
                <button
                  type="button"
                  class="btn-ghost text-[10px] px-1.5 py-0.5 text-slate-400 hover:text-cyan-300"
                  title="折叠除当前文件外的所有目录"
                  @click="collapseAll()"
                >
                  收起
                </button>
              </div>
            </div>

            <!-- 树节点列表（高度受控，自适应滚动） -->
            <div class="flex-1 min-h-0 overflow-y-auto list-viewport p-1 space-y-0.5 font-mono text-xs select-none">
              <div v-if="visibleNodes.length === 0" class="p-3 text-center text-slate-500 text-xs italic">
                暂无文件
              </div>
              <button
                v-for="node in visibleNodes"
                :key="node.fullPath"
                type="button"
                class="w-full flex items-center text-left py-1 px-1.5 rounded transition-colors group"
                :class="node.isDir
                  ? 'text-slate-300 hover:bg-slate-900/80 font-medium'
                  : (activeFile === node.fullPath
                    ? 'bg-cyan-500/20 text-cyan-300 font-semibold'
                    : 'text-slate-400 hover:bg-slate-900/80 hover:text-slate-200')"
                :style="{ paddingLeft: (node.depth * 14 + 6) + 'px' }"
                @click="onNodeClick(node)"
              >
                <!-- 展开/折叠指示器 -->
                <span v-if="node.isDir" class="w-3 text-center text-[10px] text-slate-500 group-hover:text-cyan-400 shrink-0 mr-1">
                  {{ node.isExpanded ? '▾' : '▸' }}
                </span>
                <span v-else class="w-3 shrink-0 mr-1"></span>

                <!-- 类型图标 -->
                <span class="mr-1.5 text-xs shrink-0 select-none">{{ node.icon }}</span>

                <!-- 节点名称 -->
                <span class="truncate flex-1" :title="node.fullPath">{{ node.name }}</span>

                <!-- 目录子项计数徽标 -->
                <span v-if="node.isDir" class="text-[10px] text-slate-500 font-normal ml-1 shrink-0">
                  ({{ node.childCount }})
                </span>
              </button>
            </div>
          </div>

          <!-- 右侧编辑器与调试结果 -->
          <div class="flex-1 flex flex-col min-w-0 bg-slate-900/60">
            <!-- 当前活动文件标题栏 -->
            <div class="px-4 py-1.5 bg-slate-950/70 border-b border-cyber-line/40 text-xs font-mono text-cyan-300 flex items-center justify-between">
              <span>{{ activeFile || '未选择文件' }}</span>
              <span v-if="loading" class="text-slate-500 text-[11px]">加载中...</span>
            </div>

            <!-- 代码编辑文本框 -->
            <div class="flex-1 min-h-0 p-3">
              <textarea
                v-model="codeContent"
                class="w-full h-full p-3 font-mono text-xs bg-slate-950 text-slate-200 border border-cyber-line/60 rounded focus:outline-none focus:border-cyan-500/60 resize-none leading-relaxed"
                spellcheck="false"
                :disabled="loading"
              ></textarea>
            </div>

            <!-- 调试输出控制台 (若有调试结果或调试中) -->
            <div v-if="debugResult || debugging" class="h-44 border-t border-cyber-line/60 bg-slate-950 flex flex-col shrink-0">
              <div class="px-3 py-1 bg-slate-900 border-b border-cyber-line/40 text-xs flex items-center justify-between text-slate-300">
                <span class="font-semibold flex items-center gap-1.5">
                  <span>📟 调试控制台输出</span>
                  <span v-if="debugResult" class="text-[11px] font-mono text-slate-400">
                    退出码: {{ debugResult.exitCode }} | 耗时: {{ debugResult.durationMs }}ms
                  </span>
                </span>
                <button type="button" class="btn-ghost text-[11px] p-0.5 text-slate-400 hover:text-slate-200" @click="clearDebugResult()">
                  清除
                </button>
              </div>
              <div class="flex-1 p-2.5 overflow-y-auto font-mono text-xs space-y-1">
                <div v-if="debugging" class="text-amber-400 animate-pulse">调试运行中...</div>
                <div v-if="debugResult?.output" class="text-slate-300 whitespace-pre-wrap">{{ debugResult.output }}</div>
                <div v-if="debugResult?.error" class="text-red-400 whitespace-pre-wrap">{{ debugResult.error }}</div>
                <div v-if="!debugging && !debugResult?.output && !debugResult?.error" class="text-slate-500 italic">
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
