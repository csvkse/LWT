import { computed, defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { copyText, formatDuration, formatTime } from '../utils/format.js';

const NEW_GROUP = '__new__';

// Bash 脚本示例模板（$1 $2 为位置参数，执行时可填）
const SCRIPT_TEMPLATE = `#!/usr/bin/env bash
# 位置参数：$1 $2 ...（执行时可填，空格分隔，引号包裹可含空格）
set -e

echo "hello, $1"`;

export default defineComponent({
  name: 'CommandsView',
  setup() {
    const groups = ref([]);
    const commands = ref([]);
    const remoteHosts = ref([]);
    const activeGroupId = ref('');
    const keyword = ref('');
    const loading = ref(false);

    // 快速执行
    const quickText = ref('');
    const quickTimeout = ref(60);
    const quickTargetHostId = ref('');
    const quickRunning = ref(false);
    const quickResult = ref(null);

    // 编辑器
    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive({
      name: '',
      commandText: '',
      scriptType: 0,
      description: '',
      groupId: '',
      newGroupName: '',
      timeoutSeconds: 60,
      isPinned: false,
      defaultTargetHostId: '',
    });

    // 多选与批量执行
    const selectedIds = ref(new Set());
    const showBatchModal = ref(false);
    const batchRunning = ref(false);
    const batchResult = ref(null);
    const batchForm = reactive({
      executionMode: 0,
      failurePolicy: 0,
      maxConcurrency: 5,
      targetHostId: '',
      respectIndividualHost: true,
    });

    // 执行结果
    const execModal = ref(null);
    const runningId = ref(null);
    // 脚本参数弹窗
    const argModal = ref(null);
    const argText = ref('');
    const argHostId = ref('');
    const argRunning = ref(false);
    // 分组重命名
    const renamingId = ref(null);
    const renameText = ref('');

    const filteredCommands = computed(() => commands.value);

    function getHostLabel(hostId) {
      if (!hostId) return '🖥️ 本地主机';
      const h = remoteHosts.value.find((item) => item.id === hostId);
      return h ? `🌐 ${h.name || h.host}` : '已删除主机';
    }

    async function checkPrivilegedEnvironment() {
      try {
        const res = await http(API.services.environment);
        if (res.ok && res.data) {
          isPrivilegedContainer.value = res.data.isPrivilegedContainer === true;
        }
      } catch { }
    }

    async function loadRemoteHosts() {
      const result = await http(API.remoteHosts.list, { method: 'GET' });
      if (result.ok) remoteHosts.value = result.data || [];
    }

    async function loadGroups() {
      const result = await http(API.groups.list, { method: 'GET', params: { bizType: 0 } });
      if (result.ok) groups.value = result.data;
    }

    async function loadCommands() {
      loading.value = true;
      const result = await http(API.commands.list, {
        method: 'GET',
        params: { groupId: activeGroupId.value || undefined, keyword: keyword.value || undefined },
      });
      loading.value = false;
      if (result.ok) commands.value = result.data;
    }

    async function selectGroup(id) {
      activeGroupId.value = id;
      await loadCommands();
    }

    function openCreate() {
      editingId.value = null;
      Object.assign(form, {
        name: '',
        commandText: '',
        scriptType: 0,
        description: '',
        groupId: activeGroupId.value || '',
        newGroupName: '',
        timeoutSeconds: 60,
        isPinned: false,
        defaultTargetHostId: '',
      });
      showEditor.value = true;
    }

    function openEdit(command) {
      editingId.value = command.id;
      Object.assign(form, {
        name: command.name,
        commandText: command.commandText,
        scriptType: command.scriptType || 0,
        description: command.description || '',
        groupId: command.groupId || '',
        newGroupName: '',
        timeoutSeconds: command.timeoutSeconds || 60,
        isPinned: command.isPinned,
        defaultTargetHostId: command.defaultTargetHostId || '',
      });
      showEditor.value = true;
    }

    function applyTemplate() {
      form.commandText = SCRIPT_TEMPLATE;
    }

    async function save() {
      if (!form.name.trim()) return toast.error('请输入指令名称');
      if (!form.commandText.trim()) return toast.error(form.scriptType === 1 ? '请输入脚本内容' : '请输入指令内容');
      saving.value = true;
      try {
        let groupId = form.groupId;
        if (groupId === NEW_GROUP) {
          const name = form.newGroupName.trim();
          if (!name) return toast.error('请输入新分组名称');
          const created = await http(API.groups.list, { method: 'POST', body: { name, bizType: 0, sortOrder: 0 } });
          if (!created.ok) return;
          groupId = created.data.id;
          await loadGroups();
        }
        const payload = {
          name: form.name,
          commandText: form.commandText,
          scriptType: Number(form.scriptType) || 0,
          description: form.description,
          groupId: groupId || null,
          isPinned: form.isPinned,
          timeoutSeconds: Number(form.timeoutSeconds) || null,
          defaultTargetHostId: form.defaultTargetHostId || null,
        };
        const request = editingId.value
          ? http(API.commands.item(editingId.value), { method: 'PUT', body: payload })
          : http(API.commands.list, { method: 'POST', body: payload });
        const result = await request;
        if (result.ok) {
          toast.success(editingId.value ? '已更新' : '已创建');
          showEditor.value = false;
          await loadCommands();
        }
      } finally {
        saving.value = false;
      }
    }

    function remove(command) {
      openConfirm({
        title: '删除' + (command.scriptType === 1 ? '脚本' : '指令'),
        message: `确定删除「${command.name}」？\n${command.commandText.slice(0, 300)}\n（历史执行记录会保留）`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.commands.item(command.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('已删除');
            await loadCommands();
          }
        },
      });
    }

    async function togglePin(command) {
      const result = await http(API.commands.item(command.id), {
        method: 'PUT',
        body: {
          name: command.name,
          commandText: command.commandText,
          scriptType: command.scriptType || 0,
          description: command.description,
          groupId: command.groupId,
          isPinned: !command.isPinned,
          timeoutSeconds: command.timeoutSeconds,
          defaultTargetHostId: command.defaultTargetHostId,
        },
      });
      if (result.ok) await loadCommands();
    }

    function toggleSelect(id) {
      const next = new Set(selectedIds.value);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      selectedIds.value = next;
    }

    function toggleSelectAll() {
      if (selectedIds.value.size === filteredCommands.value.length) {
        selectedIds.value = new Set();
      } else {
        selectedIds.value = new Set(filteredCommands.value.map((c) => c.id));
      }
    }

    function openBatchModal() {
      showBatchModal.value = true;
    }

    async function runBatch() {
      if (selectedIds.value.size === 0) return toast.error('请至少勾选一项指令');
      batchRunning.value = true;
      try {
        const payload = {
          commandIds: Array.from(selectedIds.value),
          commands: [],
          targetHostId: batchForm.targetHostId || null,
          respectIndividualHost: batchForm.respectIndividualHost,
          executionMode: Number(batchForm.executionMode) || 0,
          failurePolicy: Number(batchForm.failurePolicy) || 0,
          maxConcurrency: Number(batchForm.maxConcurrency) || 5,
        };
        const result = await http(API.commands.batchExecute, { method: 'POST', body: payload });
        if (result.ok) {
          showBatchModal.value = false;
          batchResult.value = result.data;
          selectedIds.value = new Set();
        }
      } finally {
        batchRunning.value = false;
      }
    }

    async function execute(command) {
      if (command.scriptType === 1) {
        argText.value = '';
        argHostId.value = command.defaultTargetHostId || '';
        argModal.value = command;
        return;
      }
      runningId.value = command.id;
      try {
        const result = await http(API.commands.execute(command.id), {
          method: 'POST',
          body: { targetHostId: command.defaultTargetHostId || null },
        });
        if (result.ok) execModal.value = { name: command.name, ...result.data };
      } finally {
        runningId.value = null;
      }
    }

    async function executeWithArgs() {
      const command = argModal.value;
      if (!command) return;
      argRunning.value = true;
      try {
        const result = await http(API.commands.execute(command.id), {
          method: 'POST',
          body: { arguments: argText.value, targetHostId: argHostId.value || null },
        });
        if (result.ok) {
          argModal.value = null;
          execModal.value = { name: command.name, ...result.data };
        }
      } finally {
        argRunning.value = false;
      }
    }

    async function runQuick() {
      if (!quickText.value.trim()) return toast.error('请输入要执行的指令');
      quickRunning.value = true;
      try {
        const result = await http(API.commands.quick, {
          method: 'POST',
          body: {
            commandText: quickText.value,
            timeoutSeconds: Number(quickTimeout.value) || null,
            targetHostId: quickTargetHostId.value || null,
          },
        });
        if (result.ok) quickResult.value = result.data;
      } finally {
        quickRunning.value = false;
      }
    }

    function confirmQuickClear() {
      openConfirm({
        title: '清空快速执行',
        message: '确定清空快速执行框与结果？',
        onConfirm: async () => {
          quickText.value = '';
          quickResult.value = null;
        },
      });
    }

    function startRename(group) {
      renamingId.value = group.id;
      renameText.value = group.name;
    }

    async function saveRename(group) {
      const name = renameText.value.trim();
      if (!name) return (renamingId.value = null);
      const result = await http(API.groups.item(group.id), {
        method: 'PUT',
        body: { name, bizType: 0, sortOrder: group.sortOrder },
      });
      if (result.ok) {
        renamingId.value = null;
        await loadGroups();
      }
    }

    function removeGroup(group) {
      openConfirm({
        title: '删除分组',
        message: `确定删除分组「${group.name}」？分组内的指令不会被删除。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.groups.item(group.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('分组已删除');
            if (activeGroupId.value === group.id) activeGroupId.value = '';
            await loadGroups();
            await loadCommands();
          }
        },
      });
    }

    async function createGroup() {
      const name = renameText.value.trim();
      if (!name) return;
      const result = await http(API.groups.list, { method: 'POST', body: { name, bizType: 0, sortOrder: 0 } });
      if (result.ok) {
        renameText.value = '';
        await loadGroups();
      }
    }

    async function copyOutput() {
      if (execModal.value && (await copyText([execModal.value.standardOutput, execModal.value.errorOutput].filter(Boolean).join('\n')))) {
        toast.success('已复制输出');
      }
    }

    onMounted(async () => {
      await Promise.all([loadGroups(), loadRemoteHosts()]);
      await loadCommands();
    });

    return {
      groups, commands, remoteHosts, activeGroupId, keyword, loading,
      quickText, quickTimeout, quickTargetHostId, quickExecutionTarget, isPrivilegedContainer, quickRunning, quickResult,
      showEditor, editingId, saving, form,
      selectedIds, showBatchModal, batchRunning, batchResult, batchForm,
      execModal, runningId, argModal, argText, argHostId, argRunning,
      renamingId, renameText, filteredCommands,
      loadCommands, selectGroup, openCreate, openEdit, applyTemplate, save, remove, togglePin,
      toggleSelect, toggleSelectAll, openBatchModal, runBatch,
      execute, executeWithArgs, runQuick, confirmQuickClear, startRename, saveRename, removeGroup,
      createGroup, copyOutput, formatTime, formatDuration, getHostLabel, NEW_GROUP,
    };
  },
  template: `
    <div class="grid grid-cols-1 lg:grid-cols-[14rem_1fr] gap-4">
      <aside class="panel p-3 flex flex-row lg:flex-col gap-1 overflow-x-auto lg:overflow-x-visible">
        <button class="text-left text-sm rounded-lg px-3 py-1.5 transition whitespace-nowrap"
                :class="!activeGroupId ? 'bg-neon/10 text-neon-soft' : 'text-slate-400 hover:text-slate-200'"
                @click="selectGroup('')">全部指令</button>
        <div v-for="group in groups" :key="group.id" class="flex items-center gap-1 group/item">
          <template v-if="renamingId === group.id">
            <input class="input !py-0.5 text-xs flex-1" v-model="renameText" @keyup.enter="saveRename(group)" @blur="saveRename(group)" autofocus />
          </template>
          <template v-else>
            <button class="text-left text-sm rounded-lg px-3 py-1.5 transition flex items-center justify-between gap-2 whitespace-nowrap"
                    :class="activeGroupId === group.id ? 'bg-neon/10 text-neon-soft' : 'text-slate-400 hover:text-slate-200'"
                    @click="selectGroup(group.id)">
              <span class="truncate max-w-[8rem] lg:max-w-none">{{ group.name }}</span>
              <span class="text-xs text-slate-600">{{ group.usageCount }}</span>
            </button>
            <button class="text-slate-600 hover:text-cyan-300 text-xs shrink-0" title="重命名" @click="startRename(group)">✎</button>
            <button class="text-slate-600 hover:text-rose-300 text-xs shrink-0" title="删除" @click="removeGroup(group)">✕</button>
          </template>
        </div>
        <div class="flex gap-1 shrink-0 lg:mt-2 lg:pt-2 lg:border-t lg:border-cyber-line/60 lg:w-full">
          <input class="input !py-1 text-xs !w-36 lg:!w-auto" v-model="renameText" placeholder="新建分组…" @keyup.enter="createGroup()" />
          <button class="btn btn-xs shrink-0" @click="createGroup()">＋</button>
        </div>
      </aside>

      <section class="flex flex-col gap-4 min-w-0">
        <div class="panel p-4">
          <div class="flex items-center justify-between mb-2">
            <h3 class="text-sm text-slate-300 font-medium">⚡ 快速执行 <span class="text-xs text-slate-500 font-normal">（临时指令，自动记入历史，支持本地与远端）</span></h3>
            <button v-if="quickText || quickResult" class="btn btn-xs" @click="confirmQuickClear()">清空</button>
          </div>
          <textarea class="input font-mono !text-[0.8rem]" rows="2" v-model="quickText"
                    placeholder="例如：df -h /home  或  tail -n 50 /var/log/syslog"></textarea>
          <div class="flex items-center gap-2 mt-2 flex-wrap">
            <select class="input !w-40 !py-1 text-xs" v-model="quickTargetHostId">
              <option value="">🖥️ 本地主机</option>
              <option v-for="h in remoteHosts" :key="h.id" :value="h.id">🌐 {{ h.name || h.host }}</option>
            </select>
            <select v-if="!quickTargetHostId && isPrivilegedContainer" class="input !w-28 !py-1 text-xs text-amber-300 border-amber-500/40" v-model="quickExecutionTarget">
              <option value="container">📦 容器</option>
              <option value="host">🖥️ 宿主</option>
            </select>
            <label class="text-xs text-slate-500">超时(秒)</label>
            <input class="input !w-20 !py-1" type="number" min="1" max="86400" v-model="quickTimeout" />
            <button class="btn btn-primary" :disabled="quickRunning" @click="runQuick()">
              {{ quickRunning ? '执行中…' : '▶ 执行' }}
            </button>
            <span v-if="quickResult" class="badge ml-1"
                  :class="quickResult.status === 0 ? 'border-emerald-500/50 text-emerald-300' : 'border-rose-500/50 text-rose-300'">
              exit {{ quickResult.exitCode ?? '—' }} · {{ formatDuration(quickResult.durationMs) }}
            </span>
          </div>
          <div v-if="quickResult" class="output-block mt-2">{{ quickResult.standardOutput }}{{ quickResult.errorOutput }}</div>
        </div>

        <div class="flex items-center gap-2 flex-wrap">
          <button class="btn btn-xs" @click="toggleSelectAll()">
            {{ selectedIds.size === filteredCommands.length && filteredCommands.length > 0 ? '取消全选' : '全选' }}
          </button>
          <input class="input !w-full sm:!w-64" v-model="keyword" placeholder="搜索名称 / 指令 / 脚本…" @keyup.enter="loadCommands()" />
          <button class="btn" @click="loadCommands()">搜索</button>
          <button class="btn btn-primary sm:ml-auto" @click="openCreate()">＋ 新建</button>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <p v-if="!filteredCommands.length && !loading" class="text-slate-600 text-sm panel p-6 text-center">
            {{ keyword ? '没有匹配的指令' : '还没有保存指令，点右上角「新建」开始' }}
          </p>
          <div v-for="command in filteredCommands" :key="command.id"
               class="panel p-4 flex flex-col gap-2 hover:border-neon/40 transition group relative"
               :class="selectedIds.has(command.id) ? '!border-cyan-500/60 bg-cyan-950/10' : ''">
            <div class="flex items-start gap-2">
              <input type="checkbox" :checked="selectedIds.has(command.id)" @change="toggleSelect(command.id)"
                     class="accent-cyan-400 mt-1 shrink-0 cursor-pointer" />
              <button class="text-lg leading-none transition mt-0.5 shrink-0" :class="command.isPinned ? 'text-amber-300' : 'text-slate-600 hover:text-amber-300'"
                      :title="command.isPinned ? '取消置顶' : '置顶'" @click="togglePin(command)">★</button>
              <div class="min-w-0 flex-1">
                <div class="text-sm text-slate-200 font-medium truncate flex items-center gap-1.5">
                  {{ command.name }}
                  <span v-if="command.scriptType === 1" class="badge border-violet-500/50 text-violet-300 !text-[0.625rem]">脚本</span>
                  <span v-if="command.defaultTargetHostId" class="badge border-cyan-500/50 text-cyan-300 !text-[0.625rem]">
                    {{ getHostLabel(command.defaultTargetHostId) }}
                  </span>
                  <template v-else>
                    <span v-if="command.executionTarget === 'host'" class="badge border-amber-500/50 text-amber-300 !text-[0.625rem]">🖥️ 宿主</span>
                    <span v-else class="badge border-slate-600/50 text-slate-400 !text-[0.625rem]">📦 容器</span>
                  </template>
                </div>
                <div class="font-mono text-xs text-cyan-300/80 break-all mt-0.5"
                     :class="command.scriptType === 1 ? 'line-clamp-3 whitespace-pre-wrap' : ''">{{ command.commandText }}</div>
                <div v-if="command.description" class="text-xs text-slate-500 mt-1 line-clamp-2">{{ command.description }}</div>
              </div>
            </div>
            <div class="flex items-center gap-2 flex-wrap text-xs text-slate-500">
              <span v-if="command.groupName" class="badge border-slate-600/60 text-slate-400">{{ command.groupName }}</span>
              <span v-if="command.timeoutSeconds">超时 {{ command.timeoutSeconds }}s</span>
              <span v-if="command.lastExecTime">上次 {{ formatTime(command.lastExecTime) }}</span>
            </div>
            <div class="flex gap-1.5 pt-1 border-t border-cyber-line/50">
              <button class="btn btn-xs btn-primary flex-1" :disabled="runningId === command.id" @click="execute(command)">
                {{ runningId === command.id ? '执行中…' : (command.scriptType === 1 ? '▶ 运行脚本' : '▶ 执行') }}
              </button>
              <button class="btn btn-xs" @click="openEdit(command)">编辑</button>
              <button class="btn btn-xs btn-danger" @click="remove(command)">删除</button>
            </div>
          </div>
        </div>

        <!-- 浮动批量操作条 -->
        <div v-if="selectedIds.size > 0"
             class="fixed bottom-6 left-1/2 -translate-x-1/2 z-[70] panel py-2.5 px-5 flex items-center gap-4 shadow-2xl border-cyan-500/60 bg-slate-900/95 backdrop-blur-md">
          <span class="text-sm text-cyan-200">已选中 <strong>{{ selectedIds.size }}</strong> 项</span>
          <button class="btn btn-primary btn-sm" @click="openBatchModal()">▶ 批量编排执行</button>
          <button class="btn btn-xs" @click="selectedIds.clear()">取消选择</button>
        </div>
      </section>

      <!-- 编辑器弹窗 -->
      <div v-if="showEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 max-h-[92vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingId ? '编辑指令' : '新建指令' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">名称 *</span>
              <input class="input" v-model="form.name" placeholder="例如：检查磁盘占用 / 重启指定服务" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">类型 *</span>
              <div class="flex gap-2">
                <button class="btn flex-1" :class="Number(form.scriptType) === 0 ? 'btn-primary' : ''" @click="form.scriptType = 0">⌨ 命令行</button>
                <button class="btn flex-1" :class="Number(form.scriptType) === 1 ? 'btn-primary' : ''" @click="form.scriptType = 1">📜 Bash 脚本</button>
              </div>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block flex items-center gap-2">
                {{ Number(form.scriptType) === 1 ? '脚本内容 *（bash 执行，可用 $1 $2 接收位置参数）' : '指令内容 *' }}
                <button v-if="Number(form.scriptType) === 1" class="btn btn-xs" @click="applyTemplate()">插入示例</button>
              </span>
              <textarea class="input font-mono !text-[0.8rem]" :rows="Number(form.scriptType) === 1 ? 12 : 3" v-model="form.commandText"
                        :placeholder="Number(form.scriptType) === 1 ? '多行 bash 脚本…' : '例如：du -sh /home/* | sort -rh | head -5'"></textarea>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">目标主机偏好</span>
              <select class="input" v-model="form.defaultTargetHostId">
                <option value="">🖥️ 本地主机 (默认)</option>
                <option v-for="h in remoteHosts" :key="h.id" :value="h.id">🌐 {{ h.name || h.host }} ({{ h.host }}:{{ h.port }})</option>
              </select>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">描述</span>
              <input class="input" v-model="form.description" />
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
            <input v-if="form.groupId === NEW_GROUP" class="input" v-model="form.newGroupName" placeholder="新分组名称" @keyup.enter="save()" />
            <div v-if="isPrivilegedContainer" class="flex flex-col gap-1 p-2.5 rounded bg-slate-900/60 border border-cyber-line/50 text-xs">
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
            <label class="flex items-center gap-2 text-sm text-slate-400">
              <input type="checkbox" v-model="form.isPinned" class="accent-cyan-400" /> 置顶显示
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="saving" @click="save()">{{ saving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>

      <!-- 批量执行配置弹窗 -->
      <div v-if="showBatchModal" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-3">批量执行编排 ({{ selectedIds.size }} 项)</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">编排模式</span>
              <div class="flex gap-2">
                <button class="btn flex-1" :class="Number(batchForm.executionMode) === 0 ? 'btn-primary' : ''" @click="batchForm.executionMode = 0">串行流水线</button>
                <button class="btn flex-1" :class="Number(batchForm.executionMode) === 1 ? 'btn-primary' : ''" @click="batchForm.executionMode = 1">并行扇出</button>
              </div>
            </label>
            <label v-if="Number(batchForm.executionMode) === 0" class="block">
              <span class="text-xs text-slate-500 mb-1 block">错误熔断策略</span>
              <select class="input" v-model="batchForm.failurePolicy">
                <option :value="0">快速失败 (FailFast) - 任意失败中止后续步骤</option>
                <option :value="1">容错继续 (Continue) - 忽略失败继续执行后续步骤</option>
              </select>
            </label>
            <label v-else class="block">
              <span class="text-xs text-slate-500 mb-1 block">最大并发数</span>
              <input class="input" type="number" min="1" max="20" v-model="batchForm.maxConcurrency" />
            </label>
            <label class="flex items-center gap-2 text-sm text-slate-300">
              <input type="checkbox" v-model="batchForm.respectIndividualHost" class="accent-cyan-400" /> 优先遵循各指令各自的偏好主机
            </label>
            <label v-if="!batchForm.respectIndividualHost" class="block">
              <span class="text-xs text-slate-500 mb-1 block">统一目标主机</span>
              <select class="input" v-model="batchForm.targetHostId">
                <option value="">🖥️ 本地主机</option>
                <option v-for="h in remoteHosts" :key="h.id" :value="h.id">🌐 {{ h.name || h.host }}</option>
              </select>
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showBatchModal = false">取消</button>
            <button class="btn btn-primary" :disabled="batchRunning" @click="runBatch()">
              {{ batchRunning ? '执行中…' : '▶ 开始批量执行' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 批量执行结果弹窗 -->
      <div v-if="batchResult" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-3xl p-5 max-h-[90vh] flex flex-col overflow-y-auto" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between mb-3">
            <h3 class="font-display text-base text-neon-soft">批量执行报告 · 耗时 {{ formatDuration(batchResult.durationMs) }}</h3>
            <span class="badge" :class="batchResult.overallStatus === 0 ? 'border-emerald-500/50 text-emerald-300' : 'border-rose-500/50 text-rose-300'">
              {{ batchResult.overallStatus === 0 ? '全部成功' : '存在失败' }}
            </span>
          </div>
          <div class="flex flex-col gap-2 mb-4">
            <div v-for="r in batchResult.results" :key="r.stepOrder"
                 class="border border-cyber-line/50 rounded-lg p-2.5 flex flex-col gap-1.5 bg-slate-900/60">
              <div class="flex items-center justify-between text-xs">
                <span class="font-medium text-slate-200">#{{ r.stepOrder }} {{ r.targetType === 1 ? '🌐 远端' : '🖥️ 本地' }}</span>
                <span class="badge" :class="r.status === 0 ? 'border-emerald-500/50 text-emerald-300' : (r.status === 4 ? 'border-amber-500/50 text-amber-300' : 'border-rose-500/50 text-rose-300')">
                  {{ r.status === 0 ? '成功' : (r.status === 4 ? '跳过' : '失败') }} · {{ formatDuration(r.durationMs) }}
                </span>
              </div>
              <div v-if="r.skipReason" class="text-xs text-amber-300 font-mono">{{ r.skipReason }}</div>
              <div v-if="r.standardOutput" class="output-block !max-h-24">{{ r.standardOutput }}</div>
              <div v-if="r.errorOutput && !r.skipReason" class="output-block !max-h-20 !border-rose-500/30 text-rose-200">{{ r.errorOutput }}</div>
            </div>
          </div>
          <div class="flex justify-end">
            <button class="btn btn-primary" @click="batchResult = null">关闭报告</button>
          </div>
        </div>
      </div>

      <!-- 脚本运行弹窗 -->
      <div v-if="argModal" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-md p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-3">运行脚本 · {{ argModal.name }}</h3>
          <div class="output-block !max-h-32 mb-3">{{ argModal.commandText }}</div>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">目标执行主机</span>
              <select class="input" v-model="argHostId">
                <option value="">🖥️ 本地主机</option>
                <option v-for="h in remoteHosts" :key="h.id" :value="h.id">🌐 {{ h.name || h.host }}</option>
              </select>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">位置参数（空格分隔，引号包裹可含空格；脚本内用 $1 $2 引用）</span>
              <input class="input font-mono" v-model="argText" placeholder="例如：/var/log 20" @keyup.enter="executeWithArgs()" />
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-4">
            <button class="btn" @click="argModal = null">取消</button>
            <button class="btn btn-primary" :disabled="argRunning" @click="executeWithArgs()">{{ argRunning ? '执行中…' : '▶ 执行' }}</button>
          </div>
        </div>
      </div>

      <!-- 单次执行结果弹窗 -->
      <div v-if="execModal" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3 min-w-0">
            <h3 class="font-display text-base text-neon-soft truncate min-w-0 flex-1" :title="'执行结果 · ' + execModal.name">执行结果 · {{ execModal.name }}</h3>
            <span class="badge shrink-0" :class="execModal.status === 0 ? 'border-emerald-500/50 text-emerald-300' : 'border-rose-500/50 text-rose-300'">
              {{ execModal.status === 0 ? '成功' : (execModal.status === 2 ? '超时' : '失败') }} · exit {{ execModal.exitCode ?? '—' }} · {{ formatDuration(execModal.durationMs) }}
            </span>
          </div>
          <div class="output-block">{{ execModal.standardOutput || '(无标准输出)' }}</div>
          <div v-if="execModal.errorOutput" class="output-block mt-2 !border-rose-500/30 text-rose-200/90">{{ execModal.errorOutput }}</div>
          <p v-if="execModal.truncated" class="text-xs text-amber-400/80 mt-2">⚠ 输出过长已被截断，完整内容请查看执行历史</p>
          <div class="flex justify-end gap-2 mt-4">
            <button class="btn" @click="copyOutput()">复制输出</button>
            <button class="btn btn-primary" @click="execModal = null">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
