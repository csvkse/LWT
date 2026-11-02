import { defineComponent, toRef } from 'vue';
import { useFileTree } from '../../utils/fileTree.js';

export default defineComponent({
  name: 'PluginFileTree',
  props: {
    fileList: {
      type: Array,
      default: () => [],
    },
    activeFile: {
      type: String,
      default: '',
    },
    uploading: {
      type: Boolean,
      default: false,
    },
  },
  emits: ['select', 'upload'],
  setup(props, { emit }) {
    const fileListRef = toRef(props, 'fileList');
    const activeFileRef = toRef(props, 'activeFile');
    const { visibleNodes, toggleFolder, expandAll, collapseAll } = useFileTree(fileListRef, activeFileRef);

    function onNodeClick(node) {
      if (!node) return;
      if (node.isDir) {
        toggleFolder(node.fullPath);
      } else {
        emit('select', node.fullPath);
      }
    }

    return {
      visibleNodes,
      onNodeClick,
      expandAll,
      collapseAll,
    };
  },
  template: `
    <div class="w-64 shrink-0 bg-slate-950 border-r border-cyber-line/60 flex flex-col overflow-hidden">
      <!-- 头部操作栏 -->
      <div class="px-2.5 py-1.5 bg-slate-900/80 border-b border-cyber-line/40 flex items-center justify-between shrink-0">
        <span class="text-[11px] font-semibold text-slate-300 uppercase tracking-wider flex items-center gap-1">
          <span>📁</span>
          <span>文件 ({{ fileList.length }})</span>
        </span>
        <div class="flex items-center gap-1">
          <button
            type="button"
            class="btn-ghost text-[10px] px-1.5 py-0.5 text-cyan-400 hover:text-cyan-300 font-medium"
            title="上传新文件到当前插件"
            :disabled="uploading"
            @click="$emit('upload')"
          >
            + 上传
          </button>
          <button
            type="button"
            class="btn-ghost text-[10px] px-1 py-0.5 text-slate-400 hover:text-cyan-300"
            title="展开全部目录"
            @click="expandAll()"
          >
            展开
          </button>
          <button
            type="button"
            class="btn-ghost text-[10px] px-1 py-0.5 text-slate-400 hover:text-cyan-300"
            title="折叠除当前文件外的所有目录"
            @click="collapseAll()"
          >
            收起
          </button>
        </div>
      </div>

      <!-- 树节点列表（自适应滚动） -->
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
          <span v-if="node.isDir" class="w-3 text-center text-[10px] text-slate-500 group-hover:text-cyan-400 shrink-0 mr-1">
            {{ node.isExpanded ? '▾' : '▸' }}
          </span>
          <span v-else class="w-3 shrink-0 mr-1"></span>
          <span class="mr-1.5 text-xs shrink-0 select-none">{{ node.icon }}</span>
          <span class="truncate flex-1" :title="node.fullPath">{{ node.name }}</span>
          <span v-if="node.isDir" class="text-[10px] text-slate-500 font-normal ml-1 shrink-0">
            ({{ node.childCount }})
          </span>
        </button>
      </div>
    </div>
  `,
});
