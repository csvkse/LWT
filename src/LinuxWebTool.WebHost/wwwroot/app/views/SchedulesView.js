import { computed, defineComponent, onMounted, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { formatTime } from '../utils/format.js';

const NEW_GROUP = '__new__';

// 常用 Cron 预设（Unix 5 段格式，服务端会自动转为 Quartz 格式）
const CRON_PRESETS = [
  { label: '每分钟', value: '* * * * *' },
  { label: '每 5 分钟', value: '*/5 * * * *' },
  { label: '每 30 分钟', value: '*/30 * * * *' },
  { label: '每小时', value: '0 * * * *' },
  { label: '每天 09:00', value: '0 9 * * *' },
  { label: '工作日 09:00', value: '0 9 * * 1-5' },
  { label: '每周日 03:00', value: '0 3 * * 0' },
  { label: '每月 1 号 00:00', value: '0 0 1 * *' },
];

export default defineComponent({
  name: 'SchedulesView',
  setup() {
    const router = useRouter();
    const tasks = ref([]);
    const commands = ref([]);
    const groups = ref([]);
    const loading = ref(false);
    const runningId = ref(null);

    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive({ name: '', commandId: '', cronExpression: '*/5 * * * *', enabled: true, groupId: '', newGroupName: '', timeoutSeconds: 60, isPinned: false, arguments: '' });

    // 下拉选项只显示「名称 + 类型」，脚本不展示全文；选中后下方预览内容
    const selectedCommand = computed(() => commands.value.find((c) => c.id === form.commandId));

    function optionLabel(command) {
      if (command.scriptType === 1) return `📜 ${command.name}（脚本）`;
      const text = command.commandText.replace(/\s+/g, ' ').trim();
      return `${command.name}（${text.length > 48 ? text.slice(0, 48) + '…' : text}）`;
    }

    async function load() {
      loading.value = true;
      try {
        const [taskResult, commandResult, groupResult] = await Promise.all([
          http(API.schedules.list),
          http(API.commands.list),
          http(API.groups.list, { params: { bizType: 1 } }),
        ]);
        if (taskResult.ok) tasks.value = taskResult.data;
        if (commandResult.ok) commands.value = commandResult.data;
        if (groupResult.ok) groups.value = groupResult.data;
      } finally {
        loading.value = false;
      }
    }

    function openCreate() {
      editingId.value = null;
      Object.assign(form, { name: '', commandId: commands.value[0]?.id || '', cronExpression: '*/5 * * * *', enabled: true, groupId: '', newGroupName: '', timeoutSeconds: 60, isPinned: false, arguments: '' });
      showEditor.value = true;
    }

    function openEdit(task) {
      editingId.value = task.id;
      Object.assign(form, {
        name: task.name,
        commandId: task.commandId,
        cronExpression: task.cronExpression,
        enabled: task.enabled,
        groupId: task.groupId || '',
        newGroupName: '',
        timeoutSeconds: task.timeoutSeconds || 60,
        isPinned: task.isPinned,
        arguments: task.arguments || '',
      });
      showEditor.value = true;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入任务名称');
      if (!form.commandId) return toast.error('请选择要执行的指令');
      saving.value = true;
      try {
        let groupId = form.groupId;
        if (groupId === NEW_GROUP) {
          const name = form.newGroupName.trim();
          if (!name) return toast.error('请输入新分组名称');
          const created = await http(API.groups.list, { method: 'POST', body: { name, bizType: 1, sortOrder: 0 } });
          if (!created.ok) return;
          groupId = created.data.id;
          await load();
        }
        const payload = {
          name: form.name,
          commandId: form.commandId,
          cronExpression: form.cronExpression,
          enabled: form.enabled,
          groupId: groupId || null,
          isPinned: form.isPinned,
          timeoutSeconds: Number(form.timeoutSeconds) || null,
          arguments: form.arguments || null,
        };
        const request = editingId.value
          ? http(API.schedules.item(editingId.value), { method: 'PUT', body: payload })
          : http(API.schedules.list, { method: 'POST', body: payload });
        const result = await request;
        if (result.ok) {
          toast.success(editingId.value ? '任务已更新并同步调度' : '任务已创建并启用调度');
          showEditor.value = false;
          await load();
        }
      } finally {
        saving.value = false;
      }
    }

    async function toggleEnabled(task) {
      const result = await http(API.schedules.toggle(task.id), { method: 'POST' });
      if (result.ok) {
        toast.success(result.data.enabled ? '任务已启用' : '任务已停用');
        await load();
      }
    }

    async function runNow(task) {
      runningId.value = task.id;
      try {
        const result = await http(API.schedules.runNow(task.id), { method: 'POST' });
        if (result.ok) toast.success(`「${task.name}」已触发，稍后在执行历史中查看结果`);
      } finally {
        runningId.value = null;
      }
    }

    async function togglePin(task) {
      const result = await http(API.schedules.item(task.id), {
        method: 'PUT',
        body: {
          name: task.name,
          commandId: task.commandId,
          cronExpression: task.cronExpression,
          enabled: task.enabled,
          groupId: task.groupId,
          isPinned: !task.isPinned,
          timeoutSeconds: task.timeoutSeconds,
          arguments: task.arguments || null,
        },
      });
      if (result.ok) await load();
    }

    function remove(task) {
      openConfirm({
        title: '删除定时任务',
        message: `确定删除任务「${task.name}」（${task.cronExpression}）？调度将同步移除。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.schedules.item(task.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('任务已删除');
            await load();
          }
        },
      });
    }

    function goRecords(task) {
      router.push({ path: '/history', query: { taskId: task.id } });
    }

    onMounted(load);

    return {
      tasks, commands, groups, loading, runningId,
      showEditor, editingId, form, saving, openCreate, openEdit, save,
      toggleEnabled, runNow, togglePin, remove, goRecords,
      selectedCommand, optionLabel,
      CRON_PRESETS, NEW_GROUP, formatTime,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-center gap-2">
        <h2 class="text-sm text-slate-400">定时任务 <span class="text-slate-600">（Cron 支持 Unix 5 段 / Quartz 6 段，自动归一化）</span></h2>
        <button class="btn btn-primary ml-auto" @click="openCreate()">＋ 新建任务</button>
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[52rem]">
          <thead>
            <tr><th class="w-8"></th><th>任务</th><th>指令</th><th>Cron</th><th>分组</th><th>状态</th><th>上次执行</th><th>下次执行</th><th class="text-right">操作</th></tr>
          </thead>
          <tbody>
            <tr v-if="!tasks.length && !loading"><td colspan="9" class="text-slate-600 py-8 text-center">暂无定时任务，点右上角「新建任务」</td></tr>
            <tr v-for="task in tasks" :key="task.id">
              <td>
                <button :class="task.isPinned ? 'text-amber-300' : 'text-slate-600 hover:text-amber-300'" @click="togglePin(task)">★</button>
              </td>
              <td class="text-slate-200">{{ task.name }}</td>
              <td class="max-w-[12rem]">
                <div class="truncate text-cyan-300/80 font-mono text-xs flex items-center gap-1.5" :title="task.commandText">
                  {{ task.commandName }}
                  <span v-if="task.scriptType === 1" class="badge border-violet-500/50 text-violet-300 !text-[0.625rem]">脚本</span>
                </div>
              </td>
              <td><span class="badge border-violet-500/50 text-violet-300 font-mono">{{ task.cronExpression }}</span></td>
              <td class="text-slate-500">{{ task.groupName || '—' }}</td>
              <td>
                <button class="badge" :class="task.enabled ? 'border-emerald-500/50 text-emerald-300' : 'border-slate-600/60 text-slate-500'"
                        @click="toggleEnabled(task)">{{ task.enabled ? '● 启用' : '○ 停用' }}</button>
              </td>
              <td class="whitespace-nowrap text-slate-400 text-xs">{{ formatTime(task.lastRunTime) }}</td>
              <td class="whitespace-nowrap text-xs" :class="task.enabled ? 'text-cyan-300/80' : 'text-slate-600'">{{ formatTime(task.nextRunTime) }}</td>
              <td class="text-right whitespace-nowrap">
                <button class="btn btn-xs btn-primary" :disabled="runningId === task.id || !task.enabled" @click="runNow(task)">
                  {{ runningId === task.id ? '触发中…' : '▶ 立即运行' }}
                </button>
                <button class="btn btn-xs" @click="goRecords(task)">记录</button>
                <button class="btn btn-xs" @click="openEdit(task)">编辑</button>
                <button class="btn btn-xs btn-danger" @click="remove(task)">删除</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="showEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 max-h-[90vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingId ? '编辑任务' : '新建任务' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">任务名称 *</span>
              <input class="input" v-model="form.name" placeholder="例如：每日清理临时目录" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">执行指令 / 脚本 * <span class="text-slate-600">（引用「指令」页保存的内容）</span></span>
              <select class="input" v-model="form.commandId">
                <option value="" disabled>请选择指令</option>
                <option v-for="command in commands" :key="command.id" :value="command.id">{{ optionLabel(command) }}</option>
              </select>
              <template v-if="selectedCommand">
                <div class="output-block !max-h-28 !text-[0.7rem] mt-1.5">{{ selectedCommand.commandText }}</div>
                <template v-if="selectedCommand.scriptType === 1">
                  <label class="block mt-1.5">
                    <span class="text-xs text-slate-500 mb-1 block">脚本位置参数（调度与「立即运行」时作为 \$1 \$2... 传入，空格分隔，引号包裹可含空格）</span>
                    <input class="input font-mono" v-model="form.arguments" placeholder="例如：/var/log 20" />
                  </label>
                  <p class="text-xs text-violet-300/80">📜 该条目为 Bash 脚本，定时执行时使用上方固定参数</p>
                </template>
              </template>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">Cron 表达式 *</span>
              <input class="input font-mono" v-model="form.cronExpression" placeholder="*/5 * * * *" />
              <div class="flex flex-wrap gap-1.5 mt-1.5">
                <button v-for="preset in CRON_PRESETS" :key="preset.value" class="btn btn-xs font-mono"
                        :class="form.cronExpression === preset.value ? 'btn-primary' : ''"
                        @click="form.cronExpression = preset.value">{{ preset.label }}</button>
              </div>
            </label>
            <div class="grid grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">分组</span>
                <select class="input" v-model="form.groupId">
                  <option value="">不分组</option>
                  <option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option>
                  <option :value="NEW_GROUP">＋ 新建分组…</option>
                </select>
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">超时（秒）</span>
                <input class="input" type="number" min="1" max="86400" v-model="form.timeoutSeconds" />
              </label>
            </div>
            <input v-if="form.groupId === NEW_GROUP" class="input" v-model="form.newGroupName" placeholder="新分组名称" />
            <div class="flex gap-5">
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.enabled" class="accent-cyan-400" /> 创建后启用
              </label>
              <label class="flex items-center gap-2 text-sm text-slate-400">
                <input type="checkbox" v-model="form.isPinned" class="accent-cyan-400" /> 置顶显示
              </label>
            </div>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="save()">{{ saving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
