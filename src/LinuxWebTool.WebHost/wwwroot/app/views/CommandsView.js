import { computed, defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { copyText, formatDuration, formatTime } from '../utils/format.js';

const NEW_GROUP = '__new__';

// Bash 脚本示例模板（$1 $2 为位置参数，执行时可填）
const SCRIPT_TEMPLATE = `#!/usr/bin/env bash
# 位置参数：\$1 \$2 ...（执行时可填，空格分隔，引号包裹可含空格）
set -e

echo "hello, \$1"`;

export default defineComponent({
  name: 'CommandsView',
  setup() {
    const groups = ref([]);
    const commands = ref([]);
    const activeGroupId = ref('');
    const keyword = ref('');
    const loading = ref(false);

    // 快速执行
    const quickText = ref('');
    const quickTimeout = ref(60);
    const quickRunning = ref(false);
    const quickResult = ref(null);

    // 编辑器
    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive({ name: '', commandText: '', scriptType: 0, description: '', groupId: '', newGroupName: '', timeoutSeconds: 60, isPinned: false });

    // 执行结果
    const execModal = ref(null);
    const runningId = ref(null);
    // 脚本参数弹窗
    const argModal = ref(null);
    const argText = ref('');
    const argRunning = ref(false);
    // 分组重命名
    const renamingId = ref(null);
    const renameText = ref('');

    const filteredCommands = computed(() => commands.value);

    async function loadGroups() {
      const result = await http(API.groups.list, { params: { bizType: 0 } });
      if (result.ok) groups.value = result.data;
    }

    async function loadCommands() {
      loading.value = true;
      const result = await http(API.commands.list, {
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
      Object.assign(form, { name: '', commandText: '', scriptType: 0, description: '', groupId: activeGroupId.value || '', newGroupName: '', timeoutSeconds: 60, isPinned: false });
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
        },
      });
      if (result.ok) await loadCommands();
    }

    async function execute(command) {
      if (command.scriptType === 1) {
        argText.value = '';
        argModal.value = command;
        return;
      }
      runningId.value = command.id;
      try {
        const result = await http(API.commands.execute(command.id), { method: 'POST' });
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
          body: { arguments: argText.value },
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
          body: { commandText: quickText.value, timeoutSeconds: Number(quickTimeout.value) || null },
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

    async function createGroup() {
      const name = renameText.value.trim();
      if (!name) return;
      const result = await http(API.groups.list, { method: 'POST', body: { name, bizType: 0, sortOrder: 0 } });
      if (result.ok) {
        toast.success('分组已创建');
        renameText.value = '';
        await loadGroups();
      }
    }

    function startRename(group) {
      renamingId.value = group.id;
      renameText.value = group.name;
    }

    async function submitRename() {
      const id = renamingId.value;
      const name = renameText.value.trim();
      renamingId.value = null;
      if (!id || !name) return;
      const result = await http(API.groups.item(id), { method: 'PUT', body: { name, bizType: 0, sortOrder: 0 } });
      if (result.ok) {
        toast.success('分组已重命名');
        await Promise.all([loadGroups(), loadCommands()]);
      }
    }

    function removeGroup(group) {
      openConfirm({
        title: '删除分组',
        message: `确定删除分组「${group.name}」？（分组需为空）`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.groups.item(group.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('分组已删除');
            if (activeGroupId.value === group.id) activeGroupId.value = '';
            await loadGroups();
          }
        },
      });
    }

    async function copyOutput() {
      const target = execModal.value;
      if (target && await copyText([target.standardOutput, target.errorOutput].filter(Boolean).join('\n'))) {
        toast.success('已复制输出');
      }
    }

    onMounted(async () => {
      await Promise.all([loadGroups(), loadCommands()]);
    });

    return {
      groups, commands, activeGroupId, keyword, loading, filteredCommands,
      quickText, quickTimeout, quickRunning, quickResult, runQuick, confirmQuickClear,
      showEditor, editingId, form, saving, openCreate, openEdit, save, applyTemplate,
      remove, togglePin, execute, executeWithArgs, runningId, execModal, copyOutput,
      argModal, argText, argRunning,
      createGroup, startRename, submitRename, removeGroup, renamingId, renameText,
      NEW_GROUP, formatTime, formatDuration,
    };
  },
  template: `
    <div class="grid grid-cols-1 lg:grid-cols-[230px_1fr] gap-4 items-start">
      <aside class="panel p-3 flex flex-row lg:flex-col gap-1.5 lg:gap-1 items-center lg:items-stretch overflow-x-auto no-scrollbar">
        <div class="hidden lg:block text-xs text-slate-500 px-1 pb-1 shrink-0">分组</div>
        <button class="shrink-0 text-left text-sm rounded-lg px-3 py-1.5 transition whitespace-nowrap"
                :class="!activeGroupId ? 'bg-neon/10 text-neon-soft' : 'text-slate-400 hover:text-slate-200'"
                @click="selectGroup('')">全部指令</button>
        <div v-for="group in groups" :key="group.id" class="flex items-center gap-1 shrink-0">
          <template v-if="renamingId === group.id">
            <input class="input !py-1 text-xs !w-28" v-model="renameText" @keyup.enter="submitRename()" @blur="submitRename()" />
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
            <h3 class="text-sm text-slate-300 font-medium">⚡ 快速执行 <span class="text-xs text-slate-500 font-normal">（临时指令，不保存，自动记入历史）</span></h3>
            <button v-if="quickText || quickResult" class="btn btn-xs" @click="confirmQuickClear()">清空</button>
          </div>
          <textarea class="input font-mono !text-[0.8rem]" rows="2" v-model="quickText"
                    placeholder="例如：df -h /home  或  tail -n 50 /var/log/syslog"></textarea>
          <div class="flex items-center gap-2 mt-2 flex-wrap">
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
          <input class="input !w-full sm:!w-64" v-model="keyword" placeholder="搜索名称 / 指令 / 脚本…" @keyup.enter="loadCommands()" />
          <button class="btn" @click="loadCommands()">搜索</button>
          <button class="btn btn-primary sm:ml-auto" @click="openCreate()">＋ 新建</button>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <p v-if="!filteredCommands.length && !loading" class="text-slate-600 text-sm panel p-6 text-center">
            {{ keyword ? '没有匹配的指令' : '还没有保存指令，点右上角「新建」开始' }}
          </p>
          <div v-for="command in filteredCommands" :key="command.id"
               class="panel p-4 flex flex-col gap-2 hover:border-neon/40 transition group">
            <div class="flex items-start gap-2">
              <button class="text-lg leading-none transition" :class="command.isPinned ? 'text-amber-300' : 'text-slate-600 hover:text-amber-300'"
                      :title="command.isPinned ? '取消置顶' : '置顶'" @click="togglePin(command)">★</button>
              <div class="min-w-0 flex-1">
                <div class="text-sm text-slate-200 font-medium truncate flex items-center gap-1.5">
                  {{ command.name }}
                  <span v-if="command.scriptType === 1" class="badge border-violet-500/50 text-violet-300 !text-[0.625rem]">脚本</span>
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
      </section>

      <div v-if="showEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 max-h-[92vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingId ? '编辑' : '新建' }}</h3>
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
                {{ Number(form.scriptType) === 1 ? '脚本内容 *（bash 执行，可用 $1 \$2 接收位置参数）' : '指令内容 *' }}
                <button v-if="Number(form.scriptType) === 1" class="btn btn-xs" @click="applyTemplate()">插入示例</button>
              </span>
              <textarea class="input font-mono !text-[0.8rem]" :rows="Number(form.scriptType) === 1 ? 12 : 3" v-model="form.commandText"
                        :placeholder="Number(form.scriptType) === 1 ? '多行 bash 脚本…' : '例如：du -sh /home/* | sort -rh | head -5'"></textarea>
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

      <div v-if="argModal" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-md p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-3">运行脚本 · {{ argModal.name }}</h3>
          <div class="output-block !max-h-32 mb-3">{{ argModal.commandText }}</div>
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">位置参数（空格分隔，引号包裹可含空格；脚本内用 \$1 \$2 引用）</span>
            <input class="input font-mono" v-model="argText" placeholder="例如：/var/log 20" @keyup.enter="executeWithArgs()" />
          </label>
          <div class="flex justify-end gap-2 mt-4">
            <button class="btn" @click="argModal = null">取消</button>
            <button class="btn btn-primary" :disabled="argRunning" @click="executeWithArgs()">{{ argRunning ? '执行中…' : '▶ 执行' }}</button>
          </div>
        </div>
      </div>

      <div v-if="execModal" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3">
            <h3 class="font-display text-base text-neon-soft">执行结果 · {{ execModal.name }}</h3>
            <span class="badge ml-auto" :class="execModal.status === 0 ? 'border-emerald-500/50 text-emerald-300' : 'border-rose-500/50 text-rose-300'">
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
