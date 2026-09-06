import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { copyText, formatTime } from '../utils/format.js';

const PAGE_SIZE = 20;

export default defineComponent({
  name: 'LogsView',
  setup() {
    const tab = ref('operations');
    const items = ref([]);
    const total = ref(0);
    const page = ref(1);
    const keyword = ref('');
    const loading = ref(false);

    const files = ref([]);
    const activeFile = ref('');
    const fileContent = ref('');
    const tailLines = ref(300);
    const fileLoading = ref(false);

    async function loadOperations() {
      loading.value = true;
      try {
        const result = await http(API.logs.operations, {
          params: { page: page.value, pageSize: PAGE_SIZE, keyword: keyword.value || undefined },
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
      page.value = 1;
      loadOperations();
    }

    function goPage(delta) {
      const next = page.value + delta;
      if (next < 1 || next > Math.max(1, Math.ceil(total.value / PAGE_SIZE))) return;
      page.value = next;
      loadOperations();
    }

    async function loadFiles() {
      const result = await http(API.logs.files);
      if (result.ok) {
        files.value = result.data;
        if (files.value.length && !files.value.some((f) => f.name === activeFile.value)) {
          await openFile(files.value[0].name);
        }
      }
    }

    async function openFile(name) {
      activeFile.value = name;
      fileLoading.value = true;
      try {
        const result = await http(API.logs.file(name), { params: { tail: tailLines.value } });
        if (result.ok) {
          fileContent.value = result.data.content;
        } else {
          fileContent.value = '';
        }
      } finally {
        fileLoading.value = false;
      }
    }

    async function reloadFile() {
      if (activeFile.value) await openFile(activeFile.value);
    }

    async function copyFile() {
      if (fileContent.value && await copyText(fileContent.value)) {
        toast.success('已复制日志内容');
      }
    }

    function formatSize(bytes) {
      if (bytes < 1024) return `${bytes} B`;
      if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
      return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
    }

    onMounted(loadOperations);

    return {
      tab, items, total, page, keyword, loading, loadOperations, search, goPage,
      files, activeFile, fileContent, tailLines, fileLoading, loadFiles, openFile, reloadFile, copyFile,
      formatTime, formatSize,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-center gap-2">
        <button class="btn" :class="tab === 'operations' ? 'btn-primary' : ''" @click="tab = 'operations'; loadOperations()">操作日志</button>
        <button class="btn" :class="tab === 'files' ? 'btn-primary' : ''" @click="tab = 'files'; loadFiles()">程序 / 调试日志</button>
      </div>

      <template v-if="tab === 'operations'">
        <div class="flex items-center gap-2">
          <input class="input !w-64" v-model="keyword" placeholder="搜索操作 / 对象 / 详情…" @keyup.enter="search()" />
          <button class="btn btn-primary" @click="search()">查询</button>
          <button class="btn" @click="loadOperations()">刷新</button>
        </div>
        <div class="panel overflow-x-auto">
          <table class="data-table min-w-[48rem]">
            <thead><tr><th>时间</th><th>操作</th><th>对象类型</th><th>对象</th><th>详情</th><th>IP</th><th>结果</th></tr></thead>
            <tbody>
              <tr v-if="!items.length && !loading"><td colspan="7" class="text-slate-600 py-8 text-center">暂无操作日志</td></tr>
              <tr v-for="item in items" :key="item.id">
                <td class="whitespace-nowrap text-slate-400 text-xs">{{ formatTime(item.time) }}</td>
                <td class="text-slate-200">{{ item.action }}</td>
                <td class="text-slate-500">{{ item.targetType }}</td>
                <td class="text-slate-300 max-w-[10rem] truncate" :title="item.targetName">{{ item.targetName }}</td>
                <td class="text-slate-500 max-w-[18rem] truncate font-mono text-xs" :title="item.detail">{{ item.detail || '—' }}</td>
                <td class="text-slate-500 text-xs">{{ item.clientIp || '—' }}</td>
                <td>
                  <span class="badge" :class="item.success ? 'border-emerald-500/50 text-emerald-300' : 'border-rose-500/50 text-rose-300'">
                    {{ item.success ? '成功' : '失败' }}
                  </span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-sm text-slate-500">
          <span>共 {{ total }} 条</span>
          <div class="flex items-center gap-2">
            <button class="btn btn-xs" :disabled="page <= 1" @click="goPage(-1)">上一页</button>
            <span>{{ page }} / {{ Math.max(1, Math.ceil(total / PAGE_SIZE)) }}</span>
            <button class="btn btn-xs" :disabled="page >= Math.ceil(total / PAGE_SIZE)" @click="goPage(1)">下一页</button>
          </div>
        </div>
      </template>

      <template v-else>
        <div class="grid grid-cols-1 lg:grid-cols-[260px_1fr] gap-4 items-start">
          <aside class="panel p-3 flex flex-col gap-1">
            <div class="flex items-center justify-between mb-1">
              <span class="text-xs text-slate-500 px-1">日志文件（按天滚动）</span>
              <button class="btn btn-xs" @click="loadFiles()">刷新</button>
            </div>
            <p v-if="!files.length" class="text-slate-600 text-xs px-1 py-2">暂无日志文件</p>
            <button v-for="file in files" :key="file.name"
                    class="text-left rounded-lg px-3 py-1.5 transition flex flex-col"
                    :class="activeFile === file.name ? 'bg-neon/10' : 'hover:bg-white/5'"
                    @click="openFile(file.name)">
              <span class="text-xs font-mono" :class="file.name.startsWith('debug') ? 'text-violet-300' : 'text-cyan-300'">{{ file.name }}</span>
              <span class="text-[10px] text-slate-600">{{ formatSize(file.lengthBytes) }} · {{ formatTime(file.lastWriteTime) }}</span>
            </button>
          </aside>
          <section class="panel p-4 flex flex-col gap-2 min-w-0">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="text-sm text-slate-300 font-mono">{{ activeFile || '未选择文件' }}</span>
              <div class="ml-auto flex items-center gap-2">
                <label class="text-xs text-slate-500">末尾</label>
                <select class="input !w-24 !py-1" v-model.number="tailLines" @change="reloadFile()">
                  <option :value="100">100 行</option>
                  <option :value="300">300 行</option>
                  <option :value="1000">1000 行</option>
                  <option :value="5000">5000 行</option>
                </select>
                <button class="btn btn-xs" @click="reloadFile()">刷新</button>
                <button class="btn btn-xs" @click="copyFile()">复制</button>
              </div>
            </div>
            <div class="output-block flex-1 min-h-[24rem]">{{ fileLoading ? '加载中…' : (fileContent || '(空)') }}</div>
          </section>
        </div>
      </template>
    </div>
  `,
});
