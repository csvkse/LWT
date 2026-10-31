import { defineComponent } from 'vue';

export default defineComponent({
  name: 'PaginationBar',
  props: {
    currentPage: { type: Number, required: true },
    totalPages: { type: Number, required: true },
    totalItems: { type: Number, required: true },
    startIndex: { type: Number, default: 0 },
    endIndex: { type: Number, default: 0 },
    pageSize: { type: Number, default: 20 },
    pageSizeOptions: { type: Array, default: () => [15, 25, 50, 100] },
    disabled: { type: Boolean, default: false },
  },
  emits: ['update:currentPage', 'update:pageSize', 'change'],
  setup(props, { emit }) {
    function goPage(page) {
      if (props.disabled) return;
      const target = Math.max(1, Math.min(page, props.totalPages));
      if (target !== props.currentPage) {
        emit('update:currentPage', target);
        emit('change', { page: target, pageSize: props.pageSize });
      }
    }

    function onPageSizeChange(e) {
      if (props.disabled) return;
      const newSize = parseInt(e.target.value, 10) || 20;
      if (newSize !== props.pageSize) {
        emit('update:pageSize', newSize);
        emit('change', { page: props.currentPage, pageSize: newSize });
      }
    }

    return {
      goPage,
      onPageSizeChange,
    };
  },
  template: `
    <div class="flex flex-wrap items-center justify-between gap-2.5 px-3 py-2 bg-cyber-bg/70 border-t border-cyber-line/40 text-xs">
      <!-- 左侧：区间与总数统计 -->
      <div class="text-slate-400 font-mono text-[11px] flex items-center gap-1.5">
        <span v-if="totalItems > 0">
          显示第 <span class="text-slate-200 font-semibold">{{ startIndex }}</span> -
          <span class="text-slate-200 font-semibold">{{ endIndex }}</span> 项，共
          <span class="text-cyber-primary font-semibold">{{ totalItems }}</span> 项
        </span>
        <span v-else class="text-slate-500">无记录</span>
      </div>

      <!-- 右侧：每页条数选择器与翻页控制器 -->
      <div class="flex items-center gap-2">
        <!-- 每页条数下拉 -->
        <div class="flex items-center gap-1 text-[11px] text-slate-400">
          <span>每页</span>
          <select
            :value="pageSize"
            @change="onPageSizeChange($event)"
            class="input py-0.5 px-1.5 text-xs font-mono bg-cyber-bg border-cyber-line/50 rounded"
            :disabled="disabled"
          >
            <option v-for="opt in pageSizeOptions" :key="opt" :value="opt">{{ opt }} 条</option>
          </select>
        </div>

        <!-- 翻页控制按钮组 -->
        <div class="inline-flex items-center gap-1">
          <button
            type="button"
            class="btn btn-xs px-2"
            :disabled="disabled || currentPage <= 1"
            @click="goPage(1)"
            title="首页"
          >
            ⏮
          </button>
          <button
            type="button"
            class="btn btn-xs px-2"
            :disabled="disabled || currentPage <= 1"
            @click="goPage(currentPage - 1)"
            title="上一页"
          >
            ◀
          </button>

          <!-- 当前页指示 -->
          <span class="px-2 py-0.5 font-mono text-[11px] text-slate-300 bg-cyber-bg border border-cyber-line/40 rounded">
            {{ currentPage }} / {{ totalPages }}
          </span>

          <button
            type="button"
            class="btn btn-xs px-2"
            :disabled="disabled || currentPage >= totalPages"
            @click="goPage(currentPage + 1)"
            title="下一页"
          >
            ▶
          </button>
          <button
            type="button"
            class="btn btn-xs px-2"
            :disabled="disabled || currentPage >= totalPages"
            @click="goPage(totalPages)"
            title="末页"
          >
            ⏭
          </button>
        </div>
      </div>
    </div>
  `,
});
