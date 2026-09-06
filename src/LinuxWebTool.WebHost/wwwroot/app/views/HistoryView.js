import { defineComponent, onMounted, reactive, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { SOURCE_OPTIONS, STATUS_OPTIONS, copyText, formatDuration, formatTime, sourceMeta, statusMeta } from '../utils/format.js';

export default defineComponent({
  name: 'HistoryView',
  setup() {
    const route = useRoute();
    const items = ref([]);
    const total = ref(0);
    const loading = ref(false);
    const taskId = ref(route.query.taskId || '');
    const detail = ref(null);
    const query = reactive({ page: 1, pageSize: 20, source: '', status: '', keyword: '' });

    async function load() {
      loading.value = true;
      try {
        const result = await http(API.history.list, {
          params: {
            page: query.page,
            pageSize: query.pageSize,
            source: query.source,
            status: query.status,
            taskId: taskId.value || undefined,
            keyword: query.keyword || undefined,
          },
        });
        if (result.ok) {
          items.value = result.data.items;
          total.value = result.data.total;
        }
      } finally {
        loading.value = false;
      }
    }

    function search() {
      query.page = 1;
      load();
    }

    function clearTaskFilter() {
      taskId.value = '';
      search();
    }

    function clearFilters() {
      query.source = '';
      query.status = '';
      query.keyword = '';
      taskId.value = '';
      search();
    }

    function clearAll() {
      openConfirm({
        title: '清空执行历史',
        message: '将删除全部执行记录（含定时任务日志），确定？',
        confirmText: '全部清空',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.history.clear, { method: 'DELETE' });
          if (result.ok) {
            toast.success('执行历史已清空');
            search();
          }
        },
      });
    }

    function clearOld() {
      openConfirm({
        title: '清理 7 天前的记录',
        message: '将删除 7 天前的执行记录，确定？',
        confirmText: '清理',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.history.clear, { method: 'DELETE', params: { olderThanDays: 7 } });
          if (result.ok) {
            toast.success('旧记录已清理');
            search();
          }
        },
      });
    }

    async function copyDetail() {
      if (detail.value && await copyText([detail.value.output, detail.value.errorOutput].filter(Boolean).join('\n'))) {
        toast.success('已复制输出');
      }
    }

    const totalPages = reactive({ get value() { return Math.max(1, Math.ceil(total.value / query.pageSize)); } });
    function goPage(delta) {
      const next = query.page + delta;
      if (next < 1 || next > totalPages.value) return;
      query.page = next;
      load();
    }

    watch(() => route.query.taskId, (value) => {
      taskId.value = value || '';
      query.page = 1;
      load();
    });

    onMounted(load);

    return {
      items, total, loading, query, taskId, detail, load, search, clearTaskFilter, clearFilters, clearAll, clearOld,
      copyDetail, goPage, totalPages,
      SOURCE_OPTIONS, STATUS_OPTIONS, formatTime, formatDuration, sourceMeta, statusMeta,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="panel p-3 flex flex-wrap items-center gap-2">
        <select class="input !w-28" v-model="query.source">
          <option v-for="opt in SOURCE_OPTIONS" :key="opt.label" :value="opt.value">{{ opt.label }}</option>
        </select>
        <select class="input !w-28" v-model="query.status">
          <option v-for="opt in STATUS_OPTIONS" :key="opt.label" :value="opt.value">{{ opt.label }}</option>
        </select>
        <input class="input !w-56" v-model="query.keyword" placeholder="搜索名称 / 指令…" @keyup.enter="search()" />
        <button class="btn btn-primary" @click="search()">查询</button>
        <button class="btn" @click="clearFilters()">重置</button>
        <span v-if="taskId" class="badge border-violet-500/50 text-violet-300">
          任务 {{ taskId.slice(0, 8) }}… <button class="ml-1 hover:text-rose-300" @click="clearTaskFilter()">✕</button>
        </span>
        <div class="ml-auto flex gap-2">
          <button class="btn btn-xs btn-danger" @click="clearOld()">清理 7 天前</button>
          <button class="btn btn-xs btn-danger" @click="clearAll()">清空全部</button>
        </div>
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[56rem]">
          <thead>
            <tr><th>时间</th><th>来源</th><th>名称</th><th>指令</th><th>状态</th><th>退出码</th><th>耗时</th><th>触发者</th></tr>
          </thead>
          <tbody>
            <tr v-if="!items.length && !loading"><td colspan="8" class="text-slate-600 py-8 text-center">没有执行记录</td></tr>
            <tr v-for="item in items" :key="item.id" class="cursor-pointer" @click="detail = item">
              <td class="whitespace-nowrap text-slate-400 text-xs">{{ formatTime(item.startTime) }}</td>
              <td><span class="badge" :class="sourceMeta(item.source).class">{{ sourceMeta(item.source).label }}</span></td>
              <td class="text-slate-200">{{ item.commandName }}</td>
              <td class="max-w-[16rem] truncate font-mono text-xs text-cyan-300/70" :title="item.commandText">{{ item.commandText }}</td>
              <td><span class="badge" :class="statusMeta(item.status).class">{{ statusMeta(item.status).label }}</span></td>
              <td class="text-slate-400">{{ item.exitCode ?? '—' }}</td>
              <td class="whitespace-nowrap text-slate-400">{{ formatDuration(item.durationMs) }}</td>
              <td class="text-slate-500 text-xs">{{ item.triggerBy }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="flex items-center justify-between text-sm text-slate-500">
        <span>共 {{ total }} 条</span>
        <div class="flex items-center gap-2">
          <button class="btn btn-xs" :disabled="query.page <= 1" @click="goPage(-1)">上一页</button>
          <span>{{ query.page }} / {{ totalPages.value }}</span>
          <button class="btn btn-xs" :disabled="query.page >= totalPages.value" @click="goPage(1)">下一页</button>
        </div>
      </div>

      <div v-if="detail" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-3xl p-5" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3 flex-wrap">
            <h3 class="font-display text-base text-neon-soft">{{ detail.commandName }}</h3>
            <span class="badge" :class="sourceMeta(detail.source).class">{{ sourceMeta(detail.source).label }}</span>
            <span class="badge" :class="statusMeta(detail.status).class">{{ statusMeta(detail.status).label }}</span>
            <span class="text-xs text-slate-500">{{ formatTime(detail.startTime) }} · exit {{ detail.exitCode ?? '—' }} · {{ formatDuration(detail.durationMs) }} · by {{ detail.triggerBy }}</span>
            <button class="btn btn-xs ml-auto" @click="detail = null">✕</button>
          </div>
          <div class="font-mono text-xs text-slate-400 border border-cyber-line rounded-lg px-3 py-2 mb-3 break-all">{{ detail.commandText }}</div>
          <div class="output-block">{{ detail.output || '(无标准输出)' }}</div>
          <div v-if="detail.errorOutput" class="output-block mt-2 !border-rose-500/30 text-rose-200/90">{{ detail.errorOutput }}</div>
          <div class="flex justify-end gap-2 mt-4">
            <button class="btn" @click="copyDetail()">复制输出</button>
            <button class="btn btn-primary" @click="detail = null">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
