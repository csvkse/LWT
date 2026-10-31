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
    const workflows = ref([]);
    const remoteHosts = ref([]);
    const groups = ref([]);
    const loading = ref(false);
    const runningId = ref(null);
    const isPrivilegedContainer = ref(false);

    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive({
      name: '',
      commandId: '',
      cronExpression: '*/5 * * * *',
      enabled: true,
      groupId: '',
      newGroupName: '',
      timeoutSeconds: 60,
      isPinned: false,
      arguments: '',
      targetBizType: 0,
      workflowId: '',
      targetHostMode: 0,
      targetHostId: '',
      targetHostIds: '',
      failurePolicy: 0,
      executionTarget: 'container',
    });

    const selectedCommand = computed(() => commands.value.find((c) => c.id === form.commandId));
    const selectedWorkflow = computed(() => workflows.value.find((w) => w.id === form.workflowId));

    // 检查是否存在关联已删除主机的孤儿任务
    const orphanedTasks = computed(() => {
      return tasks.value.filter((t) => t.targetHostMode === 1 && t.targetHostId && !remoteHosts.value.some((h) => h.id === t.targetHostId));
    });

    function getHostStatus(hostId) {
      if (!hostId) return { label: '未指定', dot: 'bg-slate-500', isDeleted: false };
      const host = remoteHosts.value.find((h) => h.id === hostId);
      if (!host) return { label: '已删除主机', dot: 'bg-rose-500', isDeleted: true };
      return {
        label: `🌐 ${host.name || host.host}`,
        dot: 'bg-emerald-400',
        isDeleted: false,
      };
    }

    function optionLabel(command) {
      if (command.scriptType === 1) return `📜 ${command.name}（脚本）`;
      const text = command.commandText.replace(/\s+/g, ' ').trim();
      return `${command.name}（${text.length > 48 ? text.slice(0, 48) + '…' : text}）`;
    }

    async function load() {
      loading.value = true;
      try {
        const [taskResult, commandResult, groupResult, hostResult, wfResult] = await Promise.all([
          http(API.schedules.list, { method: 'GET' }),
          http(API.commands.list, { method: 'GET' }),
          http(API.groups.list, { method: 'GET', params: { bizType: 1 } }),
          http(API.remoteHosts.list, { method: 'GET' }),
          http(API.workflows.list, { method: 'GET' }),
        ]);
        if (taskResult.ok) tasks.value = taskResult.data;
        if (commandResult.ok) commands.value = commandResult.data;
        if (groupResult.ok) groups.value = groupResult.data;
        if (hostResult.ok) remoteHosts.value = hostResult.data || [];
        if (wfResult.ok) workflows.value = wfResult.data || [];
        try {
          const envRes = await http(API.services.environment);
          if (envRes.ok && envRes.data) {
            isPrivilegedContainer.value = envRes.data.isPrivilegedContainer === true;
          }
        } catch { }
      } finally {
        loading.value = false;
      }
    }

    function onCommandSelectChange(e) {
      const cmdId = e.target.value;
      form.commandId = cmdId;
      // 智能非侵入预选偏好目标主机
      if (!editingId.value) {
        const cmd = commands.value.find((c) => c.id === cmdId);
        if (cmd?.defaultTargetHostId) {
          form.targetHostMode = 1;
          form.targetHostId = cmd.defaultTargetHostId;
        } else {
          form.targetHostMode = 0;
          form.targetHostId = '';
        }
        if (cmd?.executionTarget) {
          form.executionTarget = cmd.executionTarget;
        }
      }
    }

    function openCreate() {
      editingId.value = null;
      const firstCmd = commands.value[0];
      Object.assign(form, {
        name: '',
        commandId: firstCmd?.id || '',
        cronExpression: '*/5 * * * *',
        enabled: true,
        groupId: '',
        newGroupName: '',
        timeoutSeconds: 60,
        isPinned: false,
        arguments: '',
        targetBizType: 0,
        workflowId: workflows.value[0]?.id || '',
        targetHostMode: firstCmd?.defaultTargetHostId ? 1 : 0,
        targetHostId: firstCmd?.defaultTargetHostId || '',
        targetHostIds: '',
        failurePolicy: 0,
        executionTarget: firstCmd?.executionTarget || 'container',
      });
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
        targetBizType: task.targetBizType || 0,
        workflowId: task.workflowId || '',
        targetHostMode: task.targetHostMode || 0,
        targetHostId: task.targetHostId || '',
        targetHostIds: task.targetHostIds || '',
        failurePolicy: task.failurePolicy || 0,
        executionTarget: task.executionTarget || 'container',
      });
      showEditor.value = true;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入任务名称');
      if (Number(form.targetBizType) === 1) {
        if (!form.workflowId) return toast.error('请选择要执行的工作流');
      } else {
        if (!form.commandId) return toast.error('请选择要执行的指令');
      }
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
          targetBizType: Number(form.targetBizType) || 0,
          workflowId: form.workflowId || null,
          targetHostMode: Number(form.targetHostMode) || 0,
          targetHostId: form.targetHostId || null,
          targetHostIds: form.targetHostIds || null,
          failurePolicy: Number(form.failurePolicy) || 0,
          executionTarget: form.executionTarget || 'container',
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

    async function rerouteOrphanedToLocal() {
      openConfirm({
        title: '一键改派本地执行',
        message: `检测到 ${orphanedTasks.value.length} 个任务关联的主机已不存在，是否全部自动改派为「本地执行」并保留原调度？`,
        confirmText: '立即改派',
        onConfirm: async () => {
          for (const task of orphanedTasks.value) {
            await http(API.schedules.item(task.id), {
              method: 'PUT',
              body: {
                name: task.name,
                commandId: task.commandId,
                cronExpression: task.cronExpression,
                enabled: task.enabled,
                groupId: task.groupId,
                isPinned: task.isPinned,
                timeoutSeconds: task.timeoutSeconds,
                arguments: task.arguments,
                targetBizType: task.targetBizType,
                workflowId: task.workflowId,
                targetHostMode: 0,
                targetHostId: null,
                targetHostIds: null,
                failurePolicy: task.failurePolicy,
                executionTarget: task.executionTarget || 'container',
              },
            });
          }
          toast.success('已全部安全改派至本地主机');
          await load();
        },
      });
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
          targetBizType: task.targetBizType,
          workflowId: task.workflowId,
          targetHostMode: task.targetHostMode,
          targetHostId: task.targetHostId,
          targetHostIds: task.targetHostIds,
          failurePolicy: task.failurePolicy,
          executionTarget: task.executionTarget || 'container',
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
      tasks, commands, workflows, remoteHosts, groups, loading, runningId,
      showEditor, editingId, form, saving, openCreate, openEdit, save,
      toggleEnabled, runNow, togglePin, remove, goRecords,
      selectedCommand, selectedWorkflow, optionLabel, onCommandSelectChange,
      orphanedTasks, rerouteOrphanedToLocal, getHostStatus,
      CRON_PRESETS, NEW_GROUP, formatTime, isPrivilegedContainer,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <!-- 孤儿主机一键改派横幅 -->
      <div v-if="orphanedTasks.length > 0"
           class="panel p-3.5 border-amber-500/50 bg-amber-950/20 flex items-center justify-between gap-3 text-sm">
        <div class="flex items-center gap-2 text-amber-200">
          <span>⚠️ 检测到 {{ orphanedTasks.length }} 个定时任务关联的远端主机已被删除，调度已触发自动跳过防护。</span>
        </div>
        <button class="btn btn-sm btn-primary" @click="rerouteOrphanedToLocal()">一键改派本地执行</button>
      </div>

      <div class="flex items-center gap-2">
        <h2 class="text-sm text-slate-400">定时任务 <span class="text-slate-600">（支持单机、远端主机分发与工作流编排，具备熔断与缺失自动跳过）</span></h2>
        <button class="btn btn-primary ml-auto" @click="openCreate()">＋ 新建任务</button>
      </div>

      <div class="panel overflow-x-auto">
        <table class="data-table min-w-[58rem]">
          <thead>
            <tr>
              <th class="w-8 whitespace-nowrap"></th>
              <th class="whitespace-nowrap">任务名称</th>
              <th class="whitespace-nowrap">目标对象</th>
              <th class="whitespace-nowrap">执行目标</th>
              <th class="whitespace-nowrap">Cron 计划</th>
              <th class="whitespace-nowrap">分组</th>
              <th class="whitespace-nowrap">状态</th>
              <th class="whitespace-nowrap">上次执行</th>
              <th class="whitespace-nowrap">下次执行</th>
              <th class="text-right whitespace-nowrap">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="!tasks.length && !loading"><td colspan="10" class="text-slate-600 py-8 text-center">暂无定时任务，点右上角「新建任务」</td></tr>
            <tr v-for="task in tasks" :key="task.id">
              <td>
                <button class="w-5 h-5 inline-flex items-center justify-center text-sm transition-colors" :class="task.isPinned ? 'text-amber-300' : 'text-slate-600 hover:text-amber-300'" @click="togglePin(task)">★</button>
              </td>
              <td class="text-slate-200 font-medium">{{ task.name }}</td>
              <td class="max-w-[12rem]">
                <div class="truncate text-cyan-300/80 font-mono text-xs flex items-center gap-1.5" :title="task.commandText">
                  <span class="truncate">{{ task.commandName }}</span>
                  <span v-if="task.targetBizType === 1" class="badge border-fuchsia-500/50 text-fuchsia-300 !text-[0.625rem] shrink-0">工作流</span>
                  <span v-else-if="task.scriptType === 1" class="badge border-violet-500/50 text-violet-300 !text-[0.625rem] shrink-0">脚本</span>
                </div>
              </td>
              <td class="whitespace-nowrap text-xs">
                <template v-if="task.targetHostMode === 1">
                  <span class="badge" :class="getHostStatus(task.targetHostId).isDeleted ? 'border-rose-500/60 text-rose-300' : 'border-cyan-500/50 text-cyan-300'">
                    <span class="inline-block w-1.5 h-1.5 rounded-full mr-1" :class="getHostStatus(task.targetHostId).dot"></span>
                    {{ getHostStatus(task.targetHostId).label }}
                  </span>
                </template>
                <template v-else-if="task.targetHostMode === 2">
                  <span class="badge border-indigo-500/50 text-indigo-300">🌐 多主机分发</span>
                </template>
                <template v-else>
                  <span v-if="task.executionTarget === 'host'" class="badge border-amber-500/50 text-amber-300">🖥️ 宿主主机</span>
                  <span v-else class="badge border-slate-600/50 text-slate-400">📦 当前容器</span>
                </template>
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
                <div class="inline-flex items-center gap-1.5">
                  <button class="btn btn-xs btn-primary" :disabled="runningId === task.id || !task.enabled" @click="runNow(task)">
                    {{ runningId === task.id ? '触发中…' : '▶ 运行' }}
                  </button>
                  <button class="btn btn-xs" @click="goRecords(task)">记录</button>
                  <button class="btn btn-xs" @click="openEdit(task)">编辑</button>
                  <button class="btn btn-xs btn-danger" @click="remove(task)">删除</button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 编辑任务弹窗 -->
      <div v-if="showEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 max-h-[90vh] overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingId ? '编辑任务' : '新建任务' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">任务名称 *</span>
              <input class="input" v-model="form.name" placeholder="例如：每日清理临时目录" />
            </label>

            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">调度对象类型</span>
              <div class="flex gap-2">
                <button class="btn flex-1" :class="Number(form.targetBizType) === 0 ? 'btn-primary' : ''" @click="form.targetBizType = 0">⌨ 单指令 / 脚本</button>
                <button class="btn flex-1" :class="Number(form.targetBizType) === 1 ? 'btn-primary' : ''" @click="form.targetBizType = 1">🔀 工作流管道</button>
              </div>
            </label>

            <!-- 指令模式 -->
            <template v-if="Number(form.targetBizType) === 0">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">执行指令 / 脚本 *</span>
                <select class="input" :value="form.commandId" @change="onCommandSelectChange($event)">
                  <option value="" disabled>请选择指令</option>
                  <option v-for="command in commands" :key="command.id" :value="command.id">{{ optionLabel(command) }}</option>
                </select>
                <template v-if="selectedCommand">
                  <div class="output-block !max-h-24 !text-[0.7rem] mt-1.5 overflow-x-auto whitespace-pre-wrap break-all">{{ selectedCommand.commandText }}</div>
                  <template v-if="selectedCommand.scriptType === 1">
                    <label class="block mt-1.5">
                      <span class="text-xs text-slate-500 mb-1 block">脚本位置参数</span>
                      <input class="input font-mono" v-model="form.arguments" placeholder="例如：/var/log 20" />
                    </label>
                  </template>
                </template>
              </label>

              <!-- 执行目标节点选择 -->
              <div class="panel p-3 border-cyber-line/60 bg-slate-900/50 flex flex-col gap-2">
                <span class="text-xs text-cyan-300 font-medium">执行目标节点绑定</span>
                <div class="flex gap-2">
                  <button class="btn btn-xs flex-1" :class="Number(form.targetHostMode) === 0 ? 'btn-primary' : ''" @click="form.targetHostMode = 0">🖥️ 本地主机</button>
                  <button class="btn btn-xs flex-1" :class="Number(form.targetHostMode) === 1 ? 'btn-primary' : ''" @click="form.targetHostMode = 1">🌐 远端单主机</button>
                  <button class="btn btn-xs flex-1" :class="Number(form.targetHostMode) === 2 ? 'btn-primary' : ''" @click="form.targetHostMode = 2">🌐 多主机分发</button>
                </div>
                <div v-if="Number(form.targetHostMode) === 0 && isPrivilegedContainer" class="flex flex-col gap-1 p-2 rounded bg-slate-950/60 border border-cyber-line/40 text-xs">
                  <div class="flex items-center justify-between">
                    <span class="text-slate-400 font-medium">执行目标 (Target):</span>
                    <span class="text-[10px] text-cyan-400 font-mono">特权容器穿透</span>
                  </div>
                  <div class="flex items-center gap-4 mt-0.5">
                    <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
                      <input type="radio" v-model="form.executionTarget" value="container" class="accent-cyan-400" />
                      <span>📦 当前容器</span>
                    </label>
                    <label class="flex items-center gap-1.5 cursor-pointer text-slate-300">
                      <input type="radio" v-model="form.executionTarget" value="host" class="accent-cyan-400" />
                      <span>🖥️ 宿主主机 (nsenter穿透)</span>
                    </label>
                  </div>
                </div>
                <div v-if="Number(form.targetHostMode) === 1" class="mt-1">
                  <select class="input text-xs" v-model="form.targetHostId">
                    <option value="" disabled>请选择目标远端主机</option>
                    <option v-for="h in remoteHosts" :key="h.id" :value="h.id">🌐 {{ h.name || h.host }} ({{ h.host }}:{{ h.port }})</option>
                  </select>
                </div>
                <div v-if="Number(form.targetHostMode) === 2" class="mt-1">
                  <input class="input text-xs font-mono" v-model="form.targetHostIds" placeholder="主机 ID 列表，用英文逗号分隔" />
                </div>
              </div>
            </template>

            <!-- 工作流模式 -->
            <template v-else>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">工作流定义 *</span>
                <select class="input" v-model="form.workflowId">
                  <option value="" disabled>请选择工作流</option>
                  <option v-for="wf in workflows" :key="wf.id" :value="wf.id">🔀 {{ wf.name }}</option>
                </select>
                <div v-if="selectedWorkflow" class="text-xs text-slate-400 mt-1">
                  {{ selectedWorkflow.description || '无工作流描述' }}
                </div>
              </label>
            </template>

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
