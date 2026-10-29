import { computed, defineComponent, onMounted, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { copyText, formatDuration, formatTime, STATUS_META } from '../utils/format.js';

const NEW_GROUP = '__new__';

function createDefaultStep(order = 1) {
  return {
    name: `步骤 ${order}`,
    activityType: 'shell',
    stepType: 'CommandRef',
    commandId: '',
    commandText: '',
    scriptType: 0,
    targetHostId: '',
    timeoutSeconds: 30,
    failurePolicy: 'FailFast',
    httpUrl: '',
    httpMethod: 'POST',
    httpBody: '',
  };
}

function createDefaultStage(order = 1) {
  return {
    stageOrder: order,
    name: `阶段 ${order}`,
    executionMode: 0, // 0: Sequential, 1: Parallel
    failurePolicy: 0, // 0: FailFast, 1: Continue
    steps: [createDefaultStep(1)],
  };
}

export default defineComponent({
  name: 'WorkflowsView',
  setup() {
    const router = useRouter();
    const workflows = ref([]);
    const commands = ref([]);
    const remoteHosts = ref([]);
    const groups = ref([]);
    const activeGroupId = ref('');
    const keyword = ref('');
    const loading = ref(false);

    // 编辑弹窗状态
    const showEditor = ref(false);
    const editingId = ref(null);
    const saving = ref(false);
    const form = reactive({
      name: '',
      description: '',
      groupId: '',
      newGroupName: '',
      executionMode: 0,
      failurePolicy: 0,
      defaultTargetHostId: '',
      enabled: true,
      isPinned: false,
      sortOrder: 0,
      stages: [],
    });

    // 运行弹窗与结果
    const showRunModal = ref(false);
    const runningWorkflow = ref(null);
    const runTargetOverride = ref('');
    const running = ref(false);
    const runResult = ref(null);
    const activeStepDetail = ref(null);

    // 历史执行弹窗与批次详情
    const showHistoryModal = ref(false);
    const historyWorkflow = ref(null);
    const historyLoading = ref(false);
    const historyRecords = ref([]);
    const selectedBatchId = ref('');
    const historyStepDetail = ref(null);

    // 主机辅助标签
    function getHostLabel(hostId) {
      if (!hostId) return '本地主机';
      const h = remoteHosts.value.find((item) => item.id === hostId);
      return h ? `${h.name} (${h.host})` : '未知主机';
    }

    // 指令辅助标签
    function getCommandLabel(cmdId) {
      if (!cmdId) return '未绑定指令';
      const c = commands.value.find((item) => item.id === cmdId);
      return c ? c.name : '指令已删除';
    }

    // 过滤工作流列表
    const filteredWorkflows = computed(() => {
      let list = workflows.value;
      if (activeGroupId.value) {
        list = list.filter((w) => w.groupId === activeGroupId.value);
      }
      if (keyword.value.trim()) {
        const q = keyword.value.trim().toLowerCase();
        list = list.filter((w) =>
          (w.name || '').toLowerCase().includes(q) ||
          (w.description || '').toLowerCase().includes(q)
        );
      }
      return list;
    });

    // 解析工作流定义统计（阶段数与步骤数）
    function getWorkflowStats(workflow) {
      try {
        if (!workflow.definitionJson) return { stages: 0, steps: 0 };
        const def = JSON.parse(workflow.definitionJson);
        const stageList = def.stages || [];
        const stepsCount = stageList.reduce((acc, stg) => acc + (stg.steps ? stg.steps.length : 0), 0);
        return { stages: stageList.length, steps: stepsCount };
      } catch {
        return { stages: 0, steps: 0 };
      }
    }

    // 将执行记录按批次 (BatchId) 聚合为流水线运行批次
    const groupedBatches = computed(() => {
      const map = new Map();
      for (const rec of historyRecords.value) {
        const bid = rec.batchId || `single-${rec.id}`;
        if (!map.has(bid)) {
          map.set(bid, {
            batchId: bid,
            startTime: rec.startTime,
            triggerBy: rec.triggerBy || 'manual',
            steps: [],
          });
        }
        map.get(bid).steps.push(rec);
      }

      const batches = Array.from(map.values()).map((b) => {
        b.steps.sort((a, b) => (a.stepOrder || 0) - (b.stepOrder || 0));
        const hasFailure = b.steps.some((s) => s.status === 1);
        const allSkipped = b.steps.every((s) => s.status === 4);
        b.overallStatus = hasFailure ? 1 : (allSkipped ? 4 : 0);
        b.totalDurationMs = b.steps.reduce((sum, s) => sum + (s.durationMs || 0), 0);
        return b;
      });

      batches.sort((a, b) => new Date(b.startTime) - new Date(a.startTime));
      return batches;
    });

    const activeBatch = computed(() => {
      if (!selectedBatchId.value && groupedBatches.value.length > 0) {
        return groupedBatches.value[0];
      }
      return groupedBatches.value.find((b) => b.batchId === selectedBatchId.value) || null;
    });

    async function loadWorkflows() {
      loading.value = true;
      try {
        const res = await http(API.workflows.list, { method: 'GET' });
        if (res.ok) {
          workflows.value = res.data;
        }
      } finally {
        loading.value = false;
      }
    }

    async function loadCommands() {
      const res = await http(API.commands.list, { method: 'GET' });
      if (res.ok) commands.value = res.data;
    }

    async function loadRemoteHosts() {
      const res = await http(API.remoteHosts.list, { method: 'GET' });
      if (res.ok) remoteHosts.value = res.data;
    }

    async function loadGroups() {
      const res = await http(API.groups.list, { method: 'GET', params: { bizType: 0 } });
      if (res.ok) groups.value = res.data;
    }

    function selectGroup(groupId) {
      activeGroupId.value = groupId;
    }

    // 打开新建工作流弹窗
    function openCreate() {
      editingId.value = null;
      Object.assign(form, {
        name: '',
        description: '',
        groupId: activeGroupId.value || '',
        newGroupName: '',
        executionMode: 0,
        failurePolicy: 0,
        defaultTargetHostId: '',
        enabled: true,
        isPinned: false,
        sortOrder: 0,
        stages: [createDefaultStage(1)],
      });
      showEditor.value = true;
    }

    // 打开编辑工作流弹窗
    function openEdit(workflow) {
      editingId.value = workflow.id;
      let parsedStages = [];
      try {
        if (workflow.definitionJson) {
          const def = JSON.parse(workflow.definitionJson);
          if (def.stages && Array.isArray(def.stages)) {
            parsedStages = def.stages.map((stg, sIdx) => ({
              stageOrder: stg.stageOrder || sIdx + 1,
              name: stg.name || `阶段 ${sIdx + 1}`,
              executionMode: stg.executionMode !== undefined ? stg.executionMode : (stg.mode === 'Parallel' ? 1 : 0),
              failurePolicy: stg.failurePolicy !== undefined ? stg.failurePolicy : 0,
              steps: (stg.steps || []).map((step, pIdx) => ({
                name: step.name || `步骤 ${pIdx + 1}`,
                activityType: step.activityType || (step.stepType === 'Http' ? 'http' : 'shell'),
                stepType: step.stepType || (step.commandId ? 'CommandRef' : 'InlineScript'),
                commandId: step.commandId || '',
                commandText: step.commandText || step.inlineScript || '',
                scriptType: step.scriptType || 0,
                targetHostId: step.targetHostId || '',
                timeoutSeconds: step.timeoutSeconds || 30,
                failurePolicy: step.failurePolicy || 'FailFast',
                httpUrl: step.parameters?.url || '',
                httpMethod: step.parameters?.method || 'POST',
                httpBody: step.parameters?.body || '',
              })),
            }));
          }
        }
      } catch (err) {
        toast.error('工作流定义解析失败，已加载空阶段');
      }

      if (parsedStages.length === 0) {
        parsedStages = [createDefaultStage(1)];
      }

      Object.assign(form, {
        name: workflow.name,
        description: workflow.description || '',
        groupId: workflow.groupId || '',
        newGroupName: '',
        executionMode: workflow.executionMode || 0,
        failurePolicy: workflow.failurePolicy || 0,
        defaultTargetHostId: workflow.defaultTargetHostId || '',
        enabled: workflow.enabled,
        isPinned: workflow.isPinned || false,
        sortOrder: workflow.sortOrder || 0,
        stages: parsedStages,
      });
      showEditor.value = true;
    }

    // 阶段与步骤增删重排
    function addStage() {
      form.stages.push(createDefaultStage(form.stages.length + 1));
    }

    function removeStage(stageIndex) {
      if (form.stages.length <= 1) {
        return toast.error('工作流至少需要包含一个阶段');
      }
      form.stages.splice(stageIndex, 1);
    }

    function addStep(stage) {
      stage.steps.push(createDefaultStep(stage.steps.length + 1));
    }

    function removeStep(stage, stepIndex) {
      if (stage.steps.length <= 1) {
        return toast.error('阶段内至少需要包含一个步骤');
      }
      stage.steps.splice(stepIndex, 1);
    }

    function moveStep(stage, fromIndex, toIndex) {
      if (toIndex < 0 || toIndex >= stage.steps.length) return;
      const item = stage.steps.splice(fromIndex, 1)[0];
      stage.steps.splice(toIndex, 0, item);
    }

    function onStepCommandChange(step) {
      if (step.commandId) {
        const cmd = commands.value.find((c) => c.id === step.commandId);
        if (cmd) {
          if (!step.name || step.name.startsWith('步骤 ')) {
            step.name = cmd.name;
          }
          step.commandText = cmd.commandText;
          step.scriptType = cmd.scriptType || 0;
          if (!step.timeoutSeconds || step.timeoutSeconds === 30) {
            step.timeoutSeconds = cmd.timeoutSeconds || 30;
          }
        }
      }
    }

    // 保存工作流
    async function save() {
      if (!form.name.trim()) return toast.error('请输入工作流名称');
      if (form.stages.length === 0) return toast.error('请至少添加一个阶段');

      for (let sIdx = 0; sIdx < form.stages.length; sIdx++) {
        const stg = form.stages[sIdx];
        if (!stg.name.trim()) return toast.error(`第 ${sIdx + 1} 阶段名称不能为空`);
        if (!stg.steps || stg.steps.length === 0) return toast.error(`阶段 "${stg.name}" 缺少执行步骤`);

        for (let pIdx = 0; pIdx < stg.steps.length; pIdx++) {
          const step = stg.steps[pIdx];
          if (!step.name.trim()) return toast.error(`阶段 "${stg.name}" 第 ${pIdx + 1} 步名称不能为空`);
          if (step.activityType === 'shell') {
            if (step.stepType === 'CommandRef' && !step.commandId) {
              return toast.error(`步骤 "${step.name}" 未选择引用的指令`);
            }
            if (step.stepType === 'InlineScript' && !step.commandText.trim()) {
              return toast.error(`步骤 "${step.name}" 指令内容不能为空`);
            }
          } else if (step.activityType === 'http') {
            if (!step.httpUrl.trim()) {
              return toast.error(`步骤 "${step.name}" HTTP Webhook URL 不能为空`);
            }
          }
        }
      }

      saving.value = true;
      try {
        let groupId = form.groupId;
        if (groupId === NEW_GROUP) {
          const gName = form.newGroupName.trim();
          if (!gName) return toast.error('请输入新分组名称');
          const created = await http(API.groups.list, { method: 'POST', body: { name: gName, bizType: 0, sortOrder: 0 } });
          if (!created.ok) return;
          groupId = created.data.id;
          await loadGroups();
        }

        const stagesPayload = form.stages.map((stg, sIdx) => ({
          stageOrder: sIdx + 1,
          name: stg.name.trim(),
          mode: Number(stg.executionMode) === 1 ? 'Parallel' : 'Sequential',
          executionMode: Number(stg.executionMode),
          maxConcurrency: 5,
          failurePolicy: Number(stg.failurePolicy),
          steps: stg.steps.map((step) => {
            const isHttp = step.activityType === 'http';
            const params = {};
            if (isHttp) {
              params.url = step.httpUrl.trim();
              params.method = (step.httpMethod || 'POST').toUpperCase();
              if (step.httpBody) params.body = step.httpBody.trim();
            }
            return {
              name: step.name.trim(),
              stepType: isHttp ? 'Http' : (step.stepType === 'CommandRef' ? 'CommandRef' : 'InlineScript'),
              activityType: step.activityType,
              commandId: isHttp ? null : (step.stepType === 'CommandRef' && step.commandId ? step.commandId : null),
              commandText: isHttp ? null : (step.stepType === 'InlineScript' ? step.commandText.trim() : null),
              scriptType: Number(step.scriptType) || 0,
              targetHostId: step.targetHostId || null,
              timeoutSeconds: Number(step.timeoutSeconds) || 30,
              failurePolicy: step.failurePolicy || 'FailFast',
              parameters: Object.keys(params).length > 0 ? params : null,
            };
          }),
        }));

        const definition = {
          version: '1.0',
          name: form.name.trim(),
          defaultFailurePolicy: Number(form.failurePolicy) === 1 ? 'Continue' : 'FailFast',
          failurePolicy: Number(form.failurePolicy),
          stages: stagesPayload,
        };

        const payload = {
          name: form.name.trim(),
          description: form.description ? form.description.trim() : null,
          executionMode: Number(form.executionMode),
          failurePolicy: Number(form.failurePolicy),
          defaultTargetHostId: form.defaultTargetHostId || null,
          definitionJson: JSON.stringify(definition),
          enabled: form.enabled,
          groupId: groupId || null,
          isPinned: form.isPinned,
          sortOrder: Number(form.sortOrder) || 0,
        };

        const endpoint = editingId.value ? API.workflows.item(editingId.value) : API.workflows.list;
        const method = editingId.value ? 'PUT' : 'POST';
        const result = await http(endpoint, { method, body: payload });

        if (result.ok) {
          toast.success(editingId.value ? '工作流已更新' : '工作流已创建');
          showEditor.value = false;
          await loadWorkflows();
        }
      } finally {
        saving.value = false;
      }
    }

    // 删除工作流
    function confirmDelete(workflow) {
      openConfirm({
        title: '删除工作流',
        message: `确定要删除工作流 "${workflow.name}" 吗？此操作不可恢复。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const res = await http(API.workflows.item(workflow.id), { method: 'DELETE' });
          if (res.ok) {
            toast.success('工作流已删除');
            await loadWorkflows();
          }
        },
      });
    }

    // 切换启用/禁用状态
    async function toggleEnabled(workflow) {
      const res = await http(API.workflows.toggle(workflow.id), { method: 'POST' });
      if (res.ok) {
        workflow.enabled = res.data.enabled;
        toast.success(workflow.enabled ? '已启用工作流' : '已停用工作流');
      }
    }

    // 运行工作流弹窗
    function openRunModal(workflow) {
      runningWorkflow.value = workflow;
      runTargetOverride.value = '';
      runResult.value = null;
      activeStepDetail.value = null;
      showRunModal.value = true;
    }

    // 立即执行工作流
    async function executeWorkflow() {
      if (!runningWorkflow.value) return;
      running.value = true;
      try {
        const body = runTargetOverride.value ? { targetHostId: runTargetOverride.value } : null;
        const res = await http(API.workflows.run(runningWorkflow.value.id), {
          method: 'POST',
          body,
        });
        if (res.ok) {
          runResult.value = res.data;
          if (res.data.results && res.data.results.length > 0) {
            activeStepDetail.value = res.data.results[0];
          }
          await loadWorkflows();
        }
      } finally {
        running.value = false;
      }
    }

    function selectStepDetail(stepRes) {
      activeStepDetail.value = stepRes;
    }

    async function copyStepOutput() {
      if (!activeStepDetail.value) return;
      const text = [activeStepDetail.value.standardOutput, activeStepDetail.value.errorOutput]
        .filter(Boolean)
        .join('\n');
      if (await copyText(text)) {
        toast.success('已复制步骤输出');
      }
    }

    // 打开历史运行记录弹窗
    async function openHistoryModal(workflow) {
      historyWorkflow.value = workflow;
      historyRecords.value = [];
      selectedBatchId.value = '';
      historyStepDetail.value = null;
      showHistoryModal.value = true;
      historyLoading.value = true;
      try {
        const res = await http(API.workflows.history(workflow.id), { method: 'GET' });
        if (res.ok) {
          historyRecords.value = res.data.items || [];
          if (groupedBatches.value.length > 0) {
            selectedBatchId.value = groupedBatches.value[0].batchId;
            if (groupedBatches.value[0].steps.length > 0) {
              historyStepDetail.value = groupedBatches.value[0].steps[0];
            }
          }
        }
      } finally {
        historyLoading.value = false;
      }
    }

    function selectBatch(batch) {
      selectedBatchId.value = batch.batchId;
      if (batch.steps && batch.steps.length > 0) {
        historyStepDetail.value = batch.steps[0];
      } else {
        historyStepDetail.value = null;
      }
    }

    function selectHistoryStep(stepRec) {
      historyStepDetail.value = stepRec;
    }

    async function copyHistoryStepOutput() {
      if (!historyStepDetail.value) return;
      const text = [historyStepDetail.value.output, historyStepDetail.value.errorOutput]
        .filter(Boolean)
        .join('\n');
      if (await copyText(text)) {
        toast.success('已复制步骤输出');
      }
    }

    function goToGlobalHistory(workflow) {
      router.push({ path: '/history', query: { workflowId: workflow.id } });
    }

    onMounted(async () => {
      await Promise.all([loadCommands(), loadRemoteHosts(), loadGroups()]);
      await loadWorkflows();
    });

    return {
      workflows, commands, remoteHosts, groups, activeGroupId, keyword, loading,
      showEditor, editingId, saving, form,
      showRunModal, runningWorkflow, runTargetOverride, running, runResult, activeStepDetail,
      showHistoryModal, historyWorkflow, historyLoading, historyRecords, selectedBatchId, historyStepDetail,
      filteredWorkflows, groupedBatches, activeBatch, STATUS_META, NEW_GROUP,
      getHostLabel, getCommandLabel, getWorkflowStats, selectGroup,
      openCreate, openEdit, addStage, removeStage, addStep, removeStep, moveStep, onStepCommandChange,
      save, confirmDelete, toggleEnabled, openRunModal, executeWorkflow,
      selectStepDetail, copyStepOutput, openHistoryModal, selectBatch, selectHistoryStep,
      copyHistoryStepOutput, goToGlobalHistory,
      formatTime, formatDuration,
    };
  },
  template: `
    <div class="grid grid-cols-1 lg:grid-cols-[14rem_1fr] gap-4">
      <!-- 左侧工作流分组导航 -->
      <aside class="panel p-3 flex flex-row lg:flex-col gap-1 overflow-x-auto lg:overflow-x-visible">
        <button class="text-left text-sm rounded-lg px-3 py-1.5 transition whitespace-nowrap"
                :class="!activeGroupId ? 'bg-neon/10 text-neon-soft' : 'text-slate-400 hover:text-slate-200'"
                @click="selectGroup('')">全部工作流</button>
        <div v-for="group in groups" :key="group.id" class="flex items-center gap-1 group/item">
          <button class="text-left text-sm rounded-lg px-3 py-1.5 transition flex items-center justify-between gap-2 whitespace-nowrap w-full"
                  :class="activeGroupId === group.id ? 'bg-neon/10 text-neon-soft' : 'text-slate-400 hover:text-slate-200'"
                  @click="selectGroup(group.id)">
            <span class="truncate max-w-[8rem] lg:max-w-none">{{ group.name }}</span>
            <span class="text-xs text-slate-600">{{ group.usageCount }}</span>
          </button>
        </div>
      </aside>

      <!-- 右侧工作流卡片与列表 -->
      <section class="flex flex-col gap-4 min-w-0">
        <!-- 顶部操作栏 -->
        <div class="flex items-center justify-between gap-3 flex-wrap">
          <div class="flex items-center gap-2">
            <h2 class="text-base font-medium text-slate-200">工作流编排</h2>
            <span class="text-xs text-slate-500">串并行流水线调度与多活动协同</span>
          </div>
          <div class="flex items-center gap-2">
            <input class="input !py-1 text-xs !w-44" v-model="keyword" placeholder="搜索工作流名称或描述…" />
            <button class="btn btn-primary btn-sm" @click="openCreate()">＋ 新建工作流</button>
          </div>
        </div>

        <!-- 空数据提示 -->
        <div v-if="filteredWorkflows.length === 0 && !loading" class="panel p-12 text-center text-slate-500 text-sm">
          暂无匹配的工作流编排，点击右上角 “＋ 新建工作流” 开启自动化流水线。
        </div>

        <!-- 工作流卡片列表 -->
        <div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
          <div v-for="wf in filteredWorkflows" :key="wf.id"
               class="panel p-4 flex flex-col justify-between transition hover:border-cyan-500/40 relative">
            <!-- 头部：名称与状态 -->
            <div>
              <div class="flex items-start justify-between gap-2 mb-2">
                <div class="flex items-center gap-1.5 min-w-0">
                  <span v-if="wf.isPinned" class="text-amber-400 text-xs shrink-0" title="置顶">★</span>
                  <span class="font-medium text-slate-200 truncate" :title="wf.name">{{ wf.name }}</span>
                </div>
                <button class="px-2 py-0.5 rounded text-[0.7rem] font-mono shrink-0 transition"
                        :class="wf.enabled ? 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30' : 'bg-slate-700/40 text-slate-400 border border-slate-700'"
                        @click="toggleEnabled(wf)">
                  {{ wf.enabled ? '已启用' : '已停用' }}
                </button>
              </div>

              <!-- 描述 -->
              <p class="text-xs text-slate-400 mb-3 line-clamp-2 min-h-[2rem]">
                {{ wf.description || '暂无工作流描述' }}
              </p>

              <!-- 统计与标签 -->
              <div class="flex flex-wrap gap-1.5 mb-3">
                <span class="px-1.5 py-0.5 rounded bg-cyan-950/60 text-cyan-300 text-[0.7rem] border border-cyan-800/50">
                  {{ getWorkflowStats(wf).stages }} 阶段 / {{ getWorkflowStats(wf).steps }} 步骤
                </span>
                <span class="px-1.5 py-0.5 rounded text-[0.7rem] border"
                      :class="wf.executionMode === 1 ? 'bg-purple-950/50 text-purple-300 border-purple-800/40' : 'bg-slate-800/60 text-slate-300 border-slate-700'">
                  {{ wf.executionMode === 1 ? '并行调度' : '串行流水线' }}
                </span>
                <span class="px-1.5 py-0.5 rounded text-[0.7rem] border"
                      :class="wf.failurePolicy === 1 ? 'bg-amber-950/40 text-amber-300 border-amber-800/40' : 'bg-rose-950/40 text-rose-300 border-rose-800/40'">
                  {{ wf.failurePolicy === 1 ? '遇错继续' : '快速失败' }}
                </span>
                <span class="px-1.5 py-0.5 rounded bg-slate-800/70 text-slate-300 text-[0.7rem] border border-slate-700">
                  {{ getHostLabel(wf.defaultTargetHostId) }}
                </span>
              </div>
            </div>

            <!-- 底部操作与运行信息 -->
            <div class="pt-3 border-t border-cyber-line/50 flex items-center justify-between text-xs text-slate-500">
              <span>上次: {{ formatTime(wf.lastRunTime) }}</span>
              <div class="flex items-center gap-1.5">
                <button class="btn btn-primary !py-0.5 !px-2 text-xs" @click="openRunModal(wf)">运行</button>
                <button class="btn !py-0.5 !px-2 text-xs" @click="openEdit(wf)">编辑</button>
                <button class="btn !py-0.5 !px-2 text-xs" title="查看工作流执行历史" @click="openHistoryModal(wf)">历史</button>
                <button class="text-slate-500 hover:text-rose-400 p-1 text-xs" title="删除工作流" @click="confirmDelete(wf)">✕</button>
              </div>
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- 工作流创建/编辑弹窗 -->
    <div v-if="showEditor" class="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-3 sm:p-6 overflow-y-auto">
      <div class="panel max-w-4xl w-full max-h-[90vh] flex flex-col p-5 bg-cyber-bg border border-cyan-500/40 shadow-2xl rounded-xl">
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line">
          <h3 class="text-base font-medium text-slate-200">
            {{ editingId ? '编辑工作流' : '新建工作流' }}
          </h3>
          <button class="text-slate-400 hover:text-slate-200 text-lg" @click="showEditor = false">✕</button>
        </div>

        <div class="flex-1 overflow-y-auto py-4 space-y-4 pr-1">
          <!-- 基础信息 -->
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">工作流名称 *</label>
              <input class="input text-xs w-full" v-model="form.name" placeholder="例如：全自动多服务发布流水线" />
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">所属分组</label>
              <select class="input text-xs w-full" v-model="form.groupId">
                <option value="">未分组</option>
                <option v-for="g in groups" :key="g.id" :value="g.id">{{ g.name }}</option>
                <option :value="NEW_GROUP">＋ 新建分组…</option>
              </select>
              <input v-if="form.groupId === NEW_GROUP" class="input text-xs w-full mt-1.5" v-model="form.newGroupName" placeholder="输入新分组名称" />
            </div>
          </div>

          <div>
            <label class="block text-xs text-slate-400 mb-1">工作流描述</label>
            <input class="input text-xs w-full" v-model="form.description" placeholder="简要描述流水线用途与主要环节" />
          </div>

          <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">默认执行目标</label>
              <select class="input text-xs w-full" v-model="form.defaultTargetHostId">
                <option value="">本地主机 (Local)</option>
                <option v-for="h in remoteHosts" :key="h.id" :value="h.id">
                  {{ h.name }} ({{ h.host }})
                </option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">全局调度模式</label>
              <select class="input text-xs w-full" v-model="form.executionMode">
                <option :value="0">阶段串行 (按顺序依次执行各阶段)</option>
                <option :value="1">阶段并行 (所有阶段同时并发执行)</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-slate-400 mb-1">全局故障熔断策略</label>
              <select class="input text-xs w-full" v-model="form.failurePolicy">
                <option :value="0">遇错中止 (FailFast, 步骤失败立即跳过后续)</option>
                <option :value="1">遇错忽略 (Continue, 无论成败继续执行后续)</option>
              </select>
            </div>
          </div>

          <!-- 阶段与步骤编排列表 -->
          <div class="pt-2">
            <div class="flex items-center justify-between mb-2">
              <span class="text-xs font-semibold text-cyan-300">阶段与活动步骤编排</span>
              <button class="btn btn-xs btn-primary" @click="addStage()">＋ 添加执行阶段</button>
            </div>

            <div class="space-y-4">
              <div v-for="(stage, sIdx) in form.stages" :key="sIdx"
                   class="panel p-3.5 bg-slate-900/60 border border-slate-700/80 rounded-lg">
                <!-- 阶段标题栏 -->
                <div class="flex items-center justify-between gap-2 pb-2.5 mb-2.5 border-b border-slate-800">
                  <div class="flex items-center gap-2 flex-1">
                    <span class="px-2 py-0.5 rounded bg-cyan-900/40 text-cyan-300 text-xs font-mono font-bold">
                      STAGE {{ sIdx + 1 }}
                    </span>
                    <input class="input !py-1 text-xs w-48 font-medium" v-model="stage.name" placeholder="阶段名称" />
                    <select class="input !py-1 text-xs w-36" v-model="stage.executionMode">
                      <option :value="0">步骤串行</option>
                      <option :value="1">步骤并行</option>
                    </select>
                  </div>
                  <div class="flex items-center gap-1.5">
                    <button class="btn btn-xs" @click="addStep(stage)">＋ 添加步骤</button>
                    <button class="text-rose-400 hover:text-rose-300 text-xs px-1.5" title="删除该阶段" @click="removeStage(sIdx)">✕</button>
                  </div>
                </div>

                <!-- 阶段内的步骤列表 -->
                <div class="space-y-2.5 pl-2 border-l-2 border-cyan-500/30">
                  <div v-for="(step, pIdx) in stage.steps" :key="pIdx"
                       class="p-2.5 bg-slate-950/60 rounded border border-slate-800/80 space-y-2">
                    <!-- 步骤第一行：名称与类型 -->
                    <div class="flex items-center justify-between gap-2 flex-wrap">
                      <div class="flex items-center gap-2 flex-1 min-w-[12rem]">
                        <span class="text-slate-500 text-xs font-mono">{{ sIdx + 1 }}.{{ pIdx + 1 }}</span>
                        <input class="input !py-0.5 text-xs flex-1" v-model="step.name" placeholder="步骤名称" />
                      </div>
                      <div class="flex items-center gap-1.5">
                        <select class="input !py-0.5 text-xs w-32" v-model="step.activityType">
                          <option value="shell">Shell 指令 / 脚本</option>
                          <option value="http">HTTP Webhook</option>
                        </select>
                        <select class="input !py-0.5 text-xs w-32" v-model="step.targetHostId">
                          <option value="">遵循工作流默认</option>
                          <option v-for="h in remoteHosts" :key="h.id" :value="h.id">{{ h.name }}</option>
                        </select>
                        <button class="btn btn-xs !py-0 px-1 text-slate-400" title="上移" @click="moveStep(stage, pIdx, pIdx - 1)">↑</button>
                        <button class="btn btn-xs !py-0 px-1 text-slate-400" title="下移" @click="moveStep(stage, pIdx, pIdx + 1)">↓</button>
                        <button class="text-rose-400 hover:text-rose-300 text-xs px-1" title="删除步骤" @click="removeStep(stage, pIdx)">✕</button>
                      </div>
                    </div>

                    <!-- Shell 活动特有字段 -->
                    <div v-if="step.activityType === 'shell'" class="space-y-1.5">
                      <div class="flex items-center gap-2">
                        <label class="text-[0.7rem] text-slate-400 shrink-0">指令来源:</label>
                        <select class="input !py-0.5 text-xs w-36" v-model="step.stepType">
                          <option value="CommandRef">引用已有指令库</option>
                          <option value="InlineScript">自定义内联指令</option>
                        </select>
                        <select v-if="step.stepType === 'CommandRef'" class="input !py-0.5 text-xs flex-1"
                                v-model="step.commandId" @change="onStepCommandChange(step)">
                          <option value="">-- 请选择指令 --</option>
                          <option v-for="c in commands" :key="c.id" :value="c.id">{{ c.name }}</option>
                        </select>
                        <div class="flex items-center gap-1 text-[0.7rem] text-slate-400 ml-auto">
                          <span>超时(秒):</span>
                          <input type="number" class="input !py-0.5 !w-16 text-xs text-center" v-model.number="step.timeoutSeconds" />
                        </div>
                      </div>
                      <div v-if="step.stepType === 'InlineScript'">
                        <textarea class="input font-mono !text-[0.75rem] w-full" rows="2"
                                  v-model="step.commandText" placeholder="请输入要执行的 Shell 指令或脚本内容…"></textarea>
                      </div>
                    </div>

                    <!-- HTTP 活动特有字段 -->
                    <div v-else-if="step.activityType === 'http'" class="space-y-1.5">
                      <div class="flex items-center gap-2">
                        <select class="input !py-0.5 text-xs w-24 shrink-0 font-mono" v-model="step.httpMethod">
                          <option value="GET">GET</option>
                          <option value="POST">POST</option>
                          <option value="PUT">PUT</option>
                          <option value="DELETE">DELETE</option>
                        </select>
                        <input class="input !py-0.5 text-xs flex-1 font-mono" v-model="step.httpUrl"
                               placeholder="https://oapi.dingtalk.com/robot/send?access_token=... 或内网 Webhook" />
                        <div class="flex items-center gap-1 text-[0.7rem] text-slate-400 shrink-0">
                          <span>超时(秒):</span>
                          <input type="number" class="input !py-0.5 !w-16 text-xs text-center" v-model.number="step.timeoutSeconds" />
                        </div>
                      </div>
                      <div v-if="step.httpMethod !== 'GET'">
                        <textarea class="input font-mono !text-[0.75rem] w-full" rows="2"
                                  v-model="step.httpBody" placeholder='JSON 请求载荷，例如：{"msgtype":"text","text":{"content":"发布完成"}}'></textarea>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 弹窗底部操作 -->
        <div class="pt-3 border-t border-cyber-line flex items-center justify-between">
          <div class="flex items-center gap-3 text-xs text-slate-400">
            <label class="flex items-center gap-1 cursor-pointer">
              <input type="checkbox" v-model="form.isPinned" />
              <span>置顶</span>
            </label>
            <label class="flex items-center gap-1 cursor-pointer">
              <input type="checkbox" v-model="form.enabled" />
              <span>启用</span>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <button class="btn btn-sm" @click="showEditor = false">取消</button>
            <button class="btn btn-primary btn-sm" :disabled="saving" @click="save()">
              {{ saving ? '保存中…' : '保存工作流' }}
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- 运行工作流弹窗与即时结果面板 -->
    <div v-if="showRunModal" class="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-3 sm:p-6 overflow-y-auto">
      <div class="panel max-w-4xl w-full max-h-[90vh] flex flex-col p-5 bg-cyber-bg border border-cyan-500/40 shadow-2xl rounded-xl">
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line">
          <div class="flex items-center gap-2">
            <h3 class="text-base font-medium text-slate-200">运行工作流: {{ runningWorkflow?.name }}</h3>
            <span v-if="runResult" class="px-2 py-0.5 rounded text-xs font-mono border"
                  :class="STATUS_META[runResult.overallStatus]?.class || 'border-slate-700'">
              {{ STATUS_META[runResult.overallStatus]?.label || '未知状态' }}
            </span>
          </div>
          <button class="text-slate-400 hover:text-slate-200 text-lg" @click="showRunModal = false">✕</button>
        </div>

        <!-- 运行配置选项 -->
        <div class="py-3 border-b border-cyber-line flex items-center justify-between gap-3 flex-wrap">
          <div class="flex items-center gap-2 text-xs text-slate-300">
            <span>目标主机覆盖:</span>
            <select class="input !py-1 text-xs w-48" v-model="runTargetOverride">
              <option value="">按工作流/步骤默认主机</option>
              <option v-for="h in remoteHosts" :key="h.id" :value="h.id">强制使用: {{ h.name }}</option>
            </select>
          </div>
          <button class="btn btn-primary btn-sm flex items-center gap-1" :disabled="running" @click="executeWorkflow()">
            <span>⚡ {{ running ? '执行中…' : '立即启动流水线' }}</span>
          </button>
        </div>

        <!-- 即时执行结果展示 -->
        <div class="flex-1 overflow-y-auto py-3">
          <div v-if="!runResult && !running" class="p-8 text-center text-slate-500 text-xs">
            点击上方 “⚡ 立即启动流水线” 触发流水线调度。系统将按设定的阶段模式调度各活动，自动记录审计日志。
          </div>
          <div v-if="running" class="p-12 text-center text-cyan-300 text-xs animate-pulse">
            流水线任务运行中，正在按流水线阶段依次调度执行…
          </div>

          <!-- 步骤执行结果与详情联动 -->
          <div v-if="runResult && !running" class="grid grid-cols-1 md:grid-cols-[16rem_1fr] gap-3">
            <!-- 左侧步骤列表 -->
            <div class="space-y-1.5 max-h-[50vh] overflow-y-auto pr-1">
              <div v-for="(stepRes, idx) in runResult.results" :key="idx"
                   class="p-2 rounded border cursor-pointer transition text-xs"
                   :class="activeStepDetail === stepRes ? 'bg-cyan-950/40 border-cyan-500/60' : 'bg-slate-900/50 border-slate-800 hover:border-slate-700'"
                   @click="selectStepDetail(stepRes)">
                <div class="flex items-center justify-between gap-1 mb-1">
                  <span class="font-medium text-slate-200 truncate">#{{ stepRes.stepOrder }} {{ stepRes.commandName || '未命名步骤' }}</span>
                  <span class="px-1.5 py-0.2 rounded text-[0.65rem] border font-mono"
                        :class="STATUS_META[stepRes.status]?.class">
                    {{ STATUS_META[stepRes.status]?.label }}
                  </span>
                </div>
                <div class="flex items-center justify-between text-[0.7rem] text-slate-500 font-mono">
                  <span>{{ getHostLabel(stepRes.targetHostId) }}</span>
                  <span>{{ formatDuration(stepRes.durationMs) }}</span>
                </div>
              </div>
            </div>

            <!-- 右侧步骤详情与输出 -->
            <div class="panel p-3 bg-slate-950/70 border border-slate-800 flex flex-col min-h-[16rem]">
              <div v-if="activeStepDetail" class="flex flex-col h-full space-y-2">
                <div class="flex items-center justify-between pb-2 border-b border-slate-800">
                  <div class="text-xs">
                    <span class="text-slate-400">步骤输出详情: </span>
                    <span class="text-cyan-300 font-medium">#{{ activeStepDetail.stepOrder }} {{ activeStepDetail.commandName }}</span>
                  </div>
                  <button class="btn btn-xs" @click="copyStepOutput()">复制输出</button>
                </div>

                <!-- 熔断跳过说明 -->
                <div v-if="activeStepDetail.status === 4 && activeStepDetail.skipReason"
                     class="p-2 rounded bg-amber-950/40 border border-amber-600/40 text-amber-300 text-xs">
                  ⚠️ 跳过原因: {{ activeStepDetail.skipReason }}
                </div>

                <div class="flex-1 overflow-y-auto font-mono text-xs p-2 rounded bg-black/60 border border-slate-800/80 text-slate-200 whitespace-pre-wrap max-h-[35vh]">
                  <template v-if="activeStepDetail.standardOutput || activeStepDetail.errorOutput">
                    <div v-if="activeStepDetail.standardOutput">{{ activeStepDetail.standardOutput }}</div>
                    <div v-if="activeStepDetail.errorOutput" class="text-rose-400 mt-1">{{ activeStepDetail.errorOutput }}</div>
                  </template>
                  <span v-else class="text-slate-600 italic">（无输出内容）</span>
                </div>
              </div>
              <div v-else class="text-xs text-slate-500 m-auto">
                请在左侧选择一个步骤查看执行输出详情
              </div>
            </div>
          </div>
        </div>

        <div class="pt-3 border-t border-cyber-line flex justify-end">
          <button class="btn btn-sm" @click="showRunModal = false">关闭</button>
        </div>
      </div>
    </div>

    <!-- 工作流历史运行记录与详情弹窗 -->
    <div v-if="showHistoryModal" class="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-3 sm:p-6 overflow-y-auto">
      <div class="panel max-w-5xl w-full max-h-[90vh] flex flex-col p-5 bg-cyber-bg border border-cyan-500/40 shadow-2xl rounded-xl">
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line">
          <div class="flex items-center gap-2">
            <h3 class="text-base font-medium text-slate-200">执行历史: {{ historyWorkflow?.name }}</h3>
            <button class="text-xs text-cyan-400 hover:text-cyan-300 ml-2" @click="goToGlobalHistory(historyWorkflow)">前往全局历史页 ↗</button>
          </div>
          <button class="text-slate-400 hover:text-slate-200 text-lg" @click="showHistoryModal = false">✕</button>
        </div>

        <div class="flex-1 overflow-y-auto py-3">
          <div v-if="historyLoading" class="p-12 text-center text-cyan-300 text-xs animate-pulse">
            正在加载工作流执行历史记录…
          </div>

          <div v-else-if="groupedBatches.length === 0" class="p-12 text-center text-slate-500 text-xs">
            该工作流暂无历史执行记录。点击卡片上的【运行】或通过定时任务触发即可生成执行记录。
          </div>

          <div v-else class="grid grid-cols-1 md:grid-cols-[18rem_1fr] gap-4 min-h-[22rem]">
            <!-- 左侧批次列表 -->
            <div class="space-y-2 max-h-[60vh] overflow-y-auto pr-1">
              <div v-for="b in groupedBatches" :key="b.batchId"
                   class="p-2.5 rounded border cursor-pointer transition text-xs"
                   :class="selectedBatchId === b.batchId ? 'bg-cyan-950/40 border-cyan-500/60' : 'bg-slate-900/50 border-slate-800 hover:border-slate-700'"
                   @click="selectBatch(b)">
                <div class="flex items-center justify-between gap-1 mb-1">
                  <span class="font-mono text-slate-200 font-medium">{{ formatTime(b.startTime) }}</span>
                  <span class="px-1.5 py-0.2 rounded text-[0.65rem] border font-mono"
                        :class="STATUS_META[b.overallStatus]?.class">
                    {{ STATUS_META[b.overallStatus]?.label }}
                  </span>
                </div>
                <div class="flex items-center justify-between text-[0.7rem] text-slate-400 font-mono">
                  <span>{{ b.steps.length }} 个步骤 · {{ b.triggerBy }}</span>
                  <span>{{ formatDuration(b.totalDurationMs) }}</span>
                </div>
              </div>
            </div>

            <!-- 右侧批次内步骤树与详情 -->
            <div v-if="activeBatch" class="panel p-3 bg-slate-950/70 border border-slate-800 flex flex-col space-y-3">
              <div class="flex items-center justify-between pb-2 border-b border-slate-800 flex-wrap gap-2">
                <div class="flex items-center gap-2">
                  <span class="text-xs text-slate-400">批次:</span>
                  <span class="font-mono text-xs text-indigo-300">{{ activeBatch.batchId }}</span>
                  <span class="px-1.5 py-0.2 rounded text-[0.65rem] border font-mono"
                        :class="STATUS_META[activeBatch.overallStatus]?.class">
                    {{ STATUS_META[activeBatch.overallStatus]?.label }}
                  </span>
                </div>
                <div class="text-xs text-slate-400 font-mono">
                  总耗时: {{ formatDuration(activeBatch.totalDurationMs) }} · {{ activeBatch.steps.length }} 步骤
                </div>
              </div>

              <!-- 批次内步骤列表 -->
              <div class="flex flex-wrap gap-2">
                <button v-for="step in activeBatch.steps" :key="step.id"
                        class="px-2.5 py-1 rounded border text-xs flex items-center gap-1.5 transition"
                        :class="historyStepDetail?.id === step.id ? 'bg-cyan-900/50 border-cyan-400 text-cyan-200' : 'bg-slate-900/60 border-slate-800 text-slate-300 hover:border-slate-700'"
                        @click="selectHistoryStep(step)">
                  <span>#{{ step.stepOrder || 1 }} {{ step.commandName }}</span>
                  <span class="px-1 py-0.1 rounded text-[0.6rem] border"
                        :class="STATUS_META[step.status]?.class">
                    {{ STATUS_META[step.status]?.label }}
                  </span>
                </button>
              </div>

              <!-- 步骤输出与熔断详情 -->
              <div v-if="historyStepDetail" class="flex-1 flex flex-col space-y-2 pt-2 border-t border-slate-800">
                <div class="flex items-center justify-between text-xs">
                  <div class="flex items-center gap-2">
                    <span class="text-slate-400">步骤: </span>
                    <span class="text-slate-200 font-medium">#{{ historyStepDetail.stepOrder }} {{ historyStepDetail.commandName }}</span>
                    <span class="text-slate-500 font-mono">({{ getHostLabel(historyStepDetail.targetHostId) }})</span>
                    <span class="text-slate-500 font-mono">{{ formatDuration(historyStepDetail.durationMs) }}</span>
                  </div>
                  <button class="btn btn-xs" @click="copyHistoryStepOutput()">复制输出</button>
                </div>

                <div v-if="historyStepDetail.status === 4 && (historyStepDetail.errorOutput || historyStepDetail.skipReason)"
                     class="p-2 rounded bg-amber-950/40 border border-amber-600/40 text-amber-300 text-xs font-mono">
                  ⚠️ 跳过原因: {{ historyStepDetail.skipReason || historyStepDetail.errorOutput }}
                </div>

                <div class="flex-1 overflow-y-auto font-mono text-xs p-2.5 rounded bg-black/60 border border-slate-800/80 text-slate-200 whitespace-pre-wrap max-h-[30vh]">
                  <template v-if="historyStepDetail.output || historyStepDetail.errorOutput">
                    <div v-if="historyStepDetail.output">{{ historyStepDetail.output }}</div>
                    <div v-if="historyStepDetail.errorOutput" class="text-rose-400 mt-1">{{ historyStepDetail.errorOutput }}</div>
                  </template>
                  <span v-else class="text-slate-600 italic">（无标准输出）</span>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="pt-3 border-t border-cyber-line flex justify-end">
          <button class="btn btn-sm" @click="showHistoryModal = false">关闭</button>
        </div>
      </div>
    </div>
  `,
});
