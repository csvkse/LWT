import { defineComponent, onMounted, reactive } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { formatDuration, formatTime, sourceMeta, statusMeta } from '../utils/format.js';

export default defineComponent({
  name: 'DashboardView',
  setup() {
    const router = useRouter();
    const data = reactive({
      loading: true,
      commandCount: 0,
      scheduleCount: 0,
      enabledScheduleCount: 0,
      todayExecutions: 0,
      todayFailures: 0,
      recentExecutions: [],
      recentFailures: [],
      nextRuns: [],
    });

    async function load() {
      data.loading = true;
      const result = await http(API.overview);
      data.loading = false;
      if (result.ok) {
        Object.assign(data, result.data);
      }
    }

    function goHistory() {
      router.push('/history');
    }

    onMounted(load);

    return { data, load, goHistory, formatTime, formatDuration, sourceMeta, statusMeta };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <div class="panel p-4">
          <div class="text-xs text-slate-500 mb-1">已存指令</div>
          <div class="font-display text-2xl text-neon-soft">{{ data.commandCount }}</div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-slate-500 mb-1">定时任务（启用/全部）</div>
          <div class="font-display text-2xl text-violet-300">{{ data.enabledScheduleCount }}<span class="text-sm text-slate-500"> / {{ data.scheduleCount }}</span></div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-slate-500 mb-1">今日执行</div>
          <div class="font-display text-2xl text-cyan-300">{{ data.todayExecutions }}</div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-slate-500 mb-1">今日失败</div>
          <div class="font-display text-2xl" :class="data.todayFailures > 0 ? 'text-rose-300' : 'text-emerald-300'">{{ data.todayFailures }}</div>
        </div>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div class="panel p-4">
          <div class="flex items-center justify-between mb-3">
            <h3 class="text-sm text-slate-300 font-medium">最近执行</h3>
            <button class="btn btn-xs" @click="goHistory()">全部历史</button>
          </div>
          <div class="overflow-x-auto">
            <table class="data-table min-w-[28rem]">
              <thead><tr><th>时间</th><th>名称</th><th>来源</th><th>状态</th><th>耗时</th></tr></thead>
              <tbody>
                <tr v-if="!data.recentExecutions.length"><td colspan="5" class="text-slate-600">暂无执行记录</td></tr>
                <tr v-for="item in data.recentExecutions" :key="item.id">
                  <td class="whitespace-nowrap text-slate-400">{{ formatTime(item.startTime) }}</td>
                  <td class="max-w-[8rem] truncate" :title="item.commandText">{{ item.commandName }}</td>
                  <td><span class="badge" :class="sourceMeta(item.source).class">{{ sourceMeta(item.source).label }}</span></td>
                  <td><span class="badge" :class="statusMeta(item.status).class">{{ statusMeta(item.status).label }}</span></td>
                  <td class="whitespace-nowrap text-slate-400">{{ formatDuration(item.durationMs) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <div class="panel p-4">
          <h3 class="text-sm text-slate-300 font-medium mb-3">最近失败</h3>
          <div class="overflow-x-auto">
            <table class="data-table min-w-[24rem]">
              <thead><tr><th>时间</th><th>名称</th><th>状态</th><th>退出码</th></tr></thead>
              <tbody>
                <tr v-if="!data.recentFailures.length"><td colspan="4" class="text-slate-600">没有失败记录 👍</td></tr>
                <tr v-for="item in data.recentFailures" :key="item.id">
                  <td class="whitespace-nowrap text-slate-400">{{ formatTime(item.startTime) }}</td>
                  <td class="max-w-[8rem] truncate" :title="item.commandText">{{ item.commandName }}</td>
                  <td><span class="badge" :class="statusMeta(item.status).class">{{ statusMeta(item.status).label }}</span></td>
                  <td class="text-slate-400">{{ item.exitCode ?? '—' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div class="panel p-4">
        <h3 class="text-sm text-slate-300 font-medium mb-3">即将执行</h3>
        <div class="flex flex-col gap-2">
          <p v-if="!data.nextRuns.length" class="text-slate-600 text-sm">没有启用中的定时任务</p>
          <div v-for="item in data.nextRuns" :key="item.id"
               class="flex items-center gap-2 sm:gap-3 text-sm border border-cyber-line/60 rounded-lg px-3 py-2 flex-wrap">
            <span class="badge border-violet-500/50 text-violet-300 font-mono">{{ item.cronExpression }}</span>
            <span class="text-slate-300">{{ item.name }}</span>
            <span class="ml-auto text-slate-400 font-mono text-xs whitespace-nowrap">{{ formatTime(item.nextRunTime) }}</span>
          </div>
        </div>
      </div>
    </div>
  `,
});
