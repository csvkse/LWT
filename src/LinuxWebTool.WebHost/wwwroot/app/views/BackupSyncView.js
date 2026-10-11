import { computed, defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import FilePicker from '../components/FilePicker.js';

export default defineComponent({
  name: 'BackupSyncView',
  components: { FilePicker },
  setup() {
    const activeTab = ref('backup'); // 'backup' | 'sync' | 'snapshots'
    const loading = ref(false);
    const tasks = ref([]);
    const snapshots = ref([]);
    const selectedTaskId = ref(''); // 快照筛选

    // 表格多选勾选快照 ID 列表
    const selectedSnapshotIds = ref([]);

    // 文件选择器
    const picker = reactive({
      show: false,
      mode: 'folder',
      target: '',
      startPath: '/',
    });

    // ---- 任务编辑弹窗 ----
    const showTaskModal = ref(false);
    const editingTaskId = ref(null);
    const taskSaving = ref(false);
    const cronPreviewList = ref([]);
    const cronPreviewError = ref('');

    const taskForm = reactive({
      name: '',
      mode: 2, // 1 = Sync, 2 = Backup
      sourcePath: '',
      targetPath: '',
      enabled: true,
      cronPreset: 'daily_02',
      cronExpression: '0 0 2 * * ?',
      syncDirection: 1, // 1 = OneWayMirror, 2 = OneWayAddOnly
      deleteHandling: 2, // 1 = PermanentDelete, 2 = MoveToTrash
      trashRetentionDays: 30,
      keepLastCount: 5,
      keepDays: 7,
      keepWeeks: 4,
      keepMonths: 12,
      excludePatternsText: '',
    });

    // ---- 快照内容浏览弹窗 ----
    const showFileTreeModal = ref(false);
    const currentSnapshot = ref(null);
    const fileTreeNodes = ref([]);
    const fileTreeLoading = ref(false);

    // ---- 快照还原弹窗 ----
    const showRestoreModal = ref(false);
    const restoreForm = reactive({
      snapshotId: '',
      taskId: '',
      snapshotPath: '',
      restoreMode: 'source', // 'source' | 'custom'
      customPath: '',
      cleanTarget: false,
      restoring: false,
    });

    // ---- 批量清理 / GFS 修剪预演弹窗 ----
    const showPrunePreviewModal = ref(false);
    const pruneActionTitle = ref('批量清理预演');
    const pruneExecuting = ref(false);
    const pruneResult = reactive({
      totalTargeted: 0,
      deletedCount: 0,
      skippedPinnedCount: 0,
      estimatedReclaimedBytes: 0,
      affectedSnapshotIds: [],
    });
    const pendingPruneRequest = ref(null);

    // ---- 规则条件批量清理弹窗 ----
    const showRulePruneModal = ref(false);
    const rulePruneForm = reactive({
      olderThanDays: 30,
      keepLatestCount: 10,
      mode: 'olderThan', // 'olderThan' | 'keepLatest'
    });

    // 计算属性
    const backupTasks = computed(() => tasks.value.filter(t => t.mode === 2));
    const syncTasks = computed(() => tasks.value.filter(t => t.mode === 1));

    const filteredSnapshots = computed(() => {
      if (!selectedTaskId.value) return snapshots.value;
      return snapshots.value.filter(s => s.taskId === selectedTaskId.value);
    });

    const isAllSelected = computed(() => {
      if (filteredSnapshots.value.length === 0) return false;
      return filteredSnapshots.value.every(s => selectedSnapshotIds.value.includes(s.snapshotId));
    });

    // 辅助格式化
    function formatBytes(bytes) {
      if (!bytes || bytes === 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return (bytes / Math.pow(k, i)).toFixed(2) + ' ' + sizes[i];
    }

    function formatTime(dt) {
      if (!dt) return '-';
      const d = new Date(dt);
      return d.toLocaleString('zh-CN', { hour12: false });
    }

    // ---- 数据获取 ----
    async function loadTasks() {
      loading.value = true;
      try {
        const res = await http(API.backupSync.tasks, { method: 'GET' });
        if (res.ok) {
          tasks.value = res.data || [];
        }
      } catch (e) {
        toast.error(`加载任务失败：${e.message}`);
      } finally {
        loading.value = false;
      }
    }

    async function loadAllSnapshots() {
      if (tasks.value.length === 0) return;
      try {
        const backupList = tasks.value.filter(t => t.mode === 2);
        const all = [];
        for (const t of backupList) {
          const res = await http(API.backupSync.snapshots(t.id));
          if (res.ok && Array.isArray(res.data)) {
            all.push(...res.data);
          }
        }
        all.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
        snapshots.value = all;
      } catch (e) {
        console.warn('加载快照失败', e);
      }
    }

    async function refreshAll() {
      await loadTasks();
      await loadAllSnapshots();
    }

    // ---- 任务操作 ----
    async function runTask(task) {
      toast.info(`正在启动任务：${task.name}...`);
      try {
        const res = await http(API.backupSync.taskRun(task.id), { method: 'POST' });
        if (res.ok) {
          if (res.data && res.data.success) {
            toast.success(res.data.message || '任务执行成功');
          } else {
            toast.error(res.data?.message || '任务执行失败');
          }
          await refreshAll();
        }
      } catch (e) {
        toast.error(`运行任务异常：${e.message}`);
      }
    }

    async function deleteTask(task) {
      if (!confirm(`确定要删除任务【${task.name}】吗？删除任务定义不会删除磁盘上已有的备份文件。`)) {
        return;
      }
      try {
        const res = await http(API.backupSync.taskItem(task.id), { method: 'DELETE' });
        if (res.ok) {
          toast.success('任务已成功删除');
          await refreshAll();
        }
      } catch (e) {
        toast.error(`删除任务失败：${e.message}`);
      }
    }

    function viewTaskSnapshots(task) {
      selectedTaskId.value = task.id;
      activeTab.value = 'snapshots';
    }

    // ---- 任务编辑与向导 ----
    function openCreateTaskModal() {
      editingTaskId.value = null;
      taskForm.name = '';
      taskForm.mode = activeTab.value === 'sync' ? 1 : 2;
      taskForm.sourcePath = '';
      taskForm.targetPath = '';
      taskForm.enabled = true;
      taskForm.cronPreset = 'daily_02';
      taskForm.cronExpression = '0 0 2 * * ?';
      taskForm.syncDirection = 1;
      taskForm.deleteHandling = 2;
      taskForm.trashRetentionDays = 30;
      taskForm.keepLastCount = 5;
      taskForm.keepDays = 7;
      taskForm.keepWeeks = 4;
      taskForm.keepMonths = 12;
      taskForm.excludePatternsText = '';
      cronPreviewList.value = [];
      cronPreviewError.value = '';
      showTaskModal.value = true;
      fetchCronPreview();
    }

    function openEditTaskModal(task) {
      editingTaskId.value = task.id;
      taskForm.name = task.name;
      taskForm.mode = task.mode;
      taskForm.sourcePath = task.sourcePath;
      taskForm.targetPath = task.targetPath;
      taskForm.enabled = task.enabled;
      taskForm.cronExpression = task.cronExpression;
      taskForm.cronPreset = 'custom';
      taskForm.syncDirection = task.syncDirection;
      taskForm.deleteHandling = task.deleteHandling;
      taskForm.trashRetentionDays = task.trashRetentionDays;
      taskForm.keepLastCount = task.keepLastCount;
      taskForm.keepDays = task.keepDays;
      taskForm.keepWeeks = task.keepWeeks;
      taskForm.keepMonths = task.keepMonths;
      taskForm.excludePatternsText = (task.excludePatterns || []).join('; ');
      cronPreviewList.value = [];
      cronPreviewError.value = '';
      showTaskModal.value = true;
      fetchCronPreview();
    }

    function onCronPresetChange() {
      if (taskForm.cronPreset === 'hourly') {
        taskForm.cronExpression = '0 0 * * * ?';
      } else if (taskForm.cronPreset === 'daily_02') {
        taskForm.cronExpression = '0 0 2 * * ?';
      } else if (taskForm.cronPreset === 'weekly_sun') {
        taskForm.cronExpression = '0 0 3 ? * SUN';
      } else if (taskForm.cronPreset === 'monthly_1st') {
        taskForm.cronExpression = '0 0 4 1 * ?';
      }
      fetchCronPreview();
    }

    async function fetchCronPreview() {
      cronPreviewError.value = '';
      try {
        const res = await http(API.backupSync.cronPreview, {
          method: 'POST',
          body: {
            cronExpression: taskForm.cronExpression,
            count: 5,
          },
        });
        if (res.ok && res.data) {
          if (res.data.isValid) {
            cronPreviewList.value = res.data.nextRunTimes || [];
          } else {
            cronPreviewError.value = res.data.errorMessage || 'Cron 表达式无效';
            cronPreviewList.value = [];
          }
        }
      } catch (e) {
        cronPreviewError.value = e.message;
      }
    }

    async function saveTask() {
      if (!taskForm.name.trim()) {
        toast.warning('请输入任务名称');
        return;
      }
      if (!taskForm.sourcePath.trim() || !taskForm.targetPath.trim()) {
        toast.warning('请配置源路径与目标路径');
        return;
      }
      taskSaving.value = true;
      try {
        const excludeList = taskForm.excludePatternsText
          ? taskForm.excludePatternsText.split(/[;,]/).map(s => s.trim()).filter(Boolean)
          : [];

        const payload = {
          name: taskForm.name.trim(),
          mode: taskForm.mode,
          sourcePath: taskForm.sourcePath.trim(),
          targetPath: taskForm.targetPath.trim(),
          enabled: taskForm.enabled,
          cronExpression: taskForm.cronExpression,
          syncDirection: taskForm.syncDirection,
          deleteHandling: taskForm.deleteHandling,
          trashRetentionDays: taskForm.trashRetentionDays,
          keepLastCount: taskForm.keepLastCount,
          keepDays: taskForm.keepDays,
          keepWeeks: taskForm.keepWeeks,
          keepMonths: taskForm.keepMonths,
          excludePatterns: excludeList,
        };

        const isEdit = !!editingTaskId.value;
        const url = isEdit ? API.backupSync.taskItem(editingTaskId.value) : API.backupSync.tasks;
        const res = await http(url, {
          method: isEdit ? 'PUT' : 'POST',
          body: payload,
        });

        if (res.ok) {
          toast.success(isEdit ? '任务已成功更新' : '任务已成功创建');
          showTaskModal.value = false;
          await refreshAll();
        }
      } catch (e) {
        toast.error(`保存任务失败：${e.message}`);
      } finally {
        taskSaving.value = false;
      }
    }

    // ---- 目录选择器 ----
    function openPickerFor(targetField) {
      picker.target = targetField;
      picker.mode = 'folder';
      picker.startPath = taskForm[targetField] || '/';
      picker.show = true;
    }

    function onPickerConfirm(selectedPath) {
      picker.show = false;
      if (picker.target && selectedPath) {
        taskForm[picker.target] = selectedPath;
      }
    }

    // ---- 快照管理与批量清理 ----
    function toggleSelectAll() {
      if (isAllSelected.value) {
        selectedSnapshotIds.value = [];
      } else {
        selectedSnapshotIds.value = filteredSnapshots.value.map(s => s.snapshotId);
      }
    }

    function toggleSelectSnapshot(snapshotId) {
      const idx = selectedSnapshotIds.value.indexOf(snapshotId);
      if (idx > -1) {
        selectedSnapshotIds.value.splice(idx, 1);
      } else {
        selectedSnapshotIds.value.push(snapshotId);
      }
    }

    async function togglePin(snapshot) {
      const newPinned = !snapshot.IsPinned;
      try {
        const res = await http(API.backupSync.snapshotPin(snapshot.taskId, snapshot.snapshotId), {
          method: 'PUT',
          body: {
            pinned: newPinned,
            note: snapshot.note,
          },
        });
        if (res.ok) {
          toast.success(newPinned ? '快照已加锁保护' : '快照已解除锁定');
          await loadAllSnapshots();
        }
      } catch (e) {
        toast.error(`切换快照锁定状态失败：${e.message}`);
      }
    }

    // 模式 A：手动多选批量清理预演
    async function previewManualBatchDelete() {
      if (selectedSnapshotIds.value.length === 0) {
        toast.warning('请先勾选待清理的快照');
        return;
      }

      const targetTaskId = selectedTaskId.value || filteredSnapshots.value[0]?.taskId;
      if (!targetTaskId) {
        toast.warning('请选择特定备份任务');
        return;
      }

      pruneActionTitle.value = '勾选快照批量清理预演';
      const req = {
        taskId: targetTaskId,
        snapshotIds: [...selectedSnapshotIds.value],
        skipPinned: true,
      };
      pendingPruneRequest.value = { url: API.backupSync.batchDelete, req };

      try {
        const res = await http(API.backupSync.batchDeletePreview, {
          method: 'POST',
          body: req,
        });
        if (res.ok && res.data) {
          Object.assign(pruneResult, res.data);
          showPrunePreviewModal.value = true;
        }
      } catch (e) {
        toast.error(`预演失败：${e.message}`);
      }
    }

    // 模式 B：条件规则批量清理
    function openRulePruneModal() {
      if (!selectedTaskId.value && backupTasks.value.length > 0) {
        selectedTaskId.value = backupTasks.value[0].id;
      }
      showRulePruneModal.value = true;
    }

    async function previewRulePrune() {
      if (!selectedTaskId.value) {
        toast.warning('请先选择备份任务');
        return;
      }

      showRulePruneModal.value = false;
      pruneActionTitle.value = '规则条件批量清理预演';

      const req = {
        taskId: selectedTaskId.value,
        snapshotIds: [],
        skipPinned: true,
      };

      if (rulePruneForm.mode === 'olderThan') {
        const cutoff = new Date();
        cutoff.setDate(cutoff.getDate() - rulePruneForm.olderThanDays);
        req.olderThan = cutoff.toISOString();
      } else {
        req.keepLatestCount = rulePruneForm.keepLatestCount;
      }

      pendingPruneRequest.value = { url: API.backupSync.batchDelete, req };

      try {
        const res = await http(API.backupSync.batchDeletePreview, {
          method: 'POST',
          body: req,
        });
        if (res.ok && res.data) {
          Object.assign(pruneResult, res.data);
          showPrunePreviewModal.value = true;
        }
      } catch (e) {
        toast.error(`规则预演失败：${e.message}`);
      }
    }

    // 模式 C：GFS 策略一键修剪预演
    async function previewGfsPrune() {
      if (!selectedTaskId.value) {
        toast.warning('请选择需要修剪的备份任务');
        return;
      }

      pruneActionTitle.value = 'GFS 保留策略批量修剪预演';
      try {
        const res = await http(API.backupSync.prunePreview(selectedTaskId.value));
        if (res.ok && res.data) {
          const data = res.data;
          pruneResult.totalTargeted = (data.prunedSnapshots || []).length;
          pruneResult.deletedCount = (data.prunedSnapshots || []).length;
          pruneResult.skippedPinnedCount = 0;
          pruneResult.estimatedReclaimedBytes = data.reclaimableSizeBytes || 0;
          pruneResult.affectedSnapshotIds = (data.prunedSnapshots || []).map(s => s.snapshotId);

          pendingPruneRequest.value = {
            url: API.backupSync.pruneExecute(selectedTaskId.value),
            isGfsExecute: true,
          };
          showPrunePreviewModal.value = true;
        }
      } catch (e) {
        toast.error(`GFS 修剪预演失败：${e.message}`);
      }
    }

    // 确认执行清理
    async function executeConfirmedPrune() {
      if (!pendingPruneRequest.value) return;
      pruneExecuting.value = true;
      try {
        const { url, req, isGfsExecute } = pendingPruneRequest.value;
        const res = isGfsExecute
          ? await http(url, { method: 'POST' })
          : await http(url, { method: 'POST', body: req });

        if (res.ok) {
          toast.success(`清理执行完成，释放空间：${formatBytes(pruneResult.estimatedReclaimedBytes)}`);
          showPrunePreviewModal.value = false;
          selectedSnapshotIds.value = [];
          await loadAllSnapshots();
        }
      } catch (e) {
        toast.error(`执行清理失败：${e.message}`);
      } finally {
        pruneExecuting.value = false;
      }
    }

    // 快照单项删除
    async function deleteSingleSnapshot(snap) {
      if (snap.IsPinned) {
        toast.warning('该快照已加锁保护，无法删除。请先解锁！');
        return;
      }
      if (!confirm(`确定删除快照【${snap.snapshotId}】吗？`)) {
        return;
      }
      try {
        const res = await http(API.backupSync.batchDelete, {
          method: 'POST',
          body: {
            taskId: snap.taskId,
            snapshotIds: [snap.snapshotId],
            skipPinned: true,
          },
        });
        if (res.ok) {
          toast.success('快照已删除');
          await loadAllSnapshots();
        }
      } catch (e) {
        toast.error(`删除快照失败：${e.message}`);
      }
    }

    // ---- 快照内容浏览 ----
    async function openFileTree(snap) {
      currentSnapshot.value = snap;
      showFileTreeModal.value = true;
      fileTreeLoading.value = true;
      try {
        const res = await http(API.backupSync.snapshotFiles(snap.taskId, snap.snapshotId));
        if (res.ok) {
          fileTreeNodes.value = res.data || [];
        }
      } catch (e) {
        toast.error(`读取快照文件树失败：${e.message}`);
      } finally {
        fileTreeLoading.value = false;
      }
    }

    // ---- 快照还原 ----
    function openRestoreModal(snap) {
      restoreForm.snapshotId = snap.snapshotId;
      restoreForm.taskId = snap.taskId;
      restoreForm.snapshotPath = snap.snapshotPath;
      restoreForm.restoreMode = 'source';
      restoreForm.customPath = '';
      restoreForm.cleanTarget = false;
      restoreForm.restoring = false;
      showRestoreModal.value = true;
    }

    async function executeRestore() {
      restoreForm.restoring = true;
      try {
        const payload = {
          targetRestorePath: restoreForm.restoreMode === 'custom' ? restoreForm.customPath.trim() : null,
          cleanTargetBeforeRestore: restoreForm.cleanTarget,
        };
        const res = await http(API.backupSync.snapshotRestore(restoreForm.taskId, restoreForm.snapshotId), {
          method: 'POST',
          body: payload,
        });
        if (res.ok && res.data) {
          toast.success(`还原完成！恢复文件数: ${res.data.restoredFilesCount}，耗时: ${res.data.durationMs}ms`);
          showRestoreModal.value = false;
        }
      } catch (e) {
        toast.error(`还原快照异常：${e.message}`);
      } finally {
        restoreForm.restoring = false;
      }
    }

    function getTaskName(taskId) {
      const t = tasks.value.find(item => item.id === taskId);
      return t ? t.name : taskId;
    }

    onMounted(async () => {
      await refreshAll();
    });

    return {
      activeTab,
      loading,
      tasks,
      backupTasks,
      syncTasks,
      snapshots,
      filteredSnapshots,
      selectedTaskId,
      selectedSnapshotIds,
      isAllSelected,
      picker,
      showTaskModal,
      editingTaskId,
      taskSaving,
      taskForm,
      cronPreviewList,
      cronPreviewError,
      showFileTreeModal,
      currentSnapshot,
      fileTreeNodes,
      fileTreeLoading,
      showRestoreModal,
      restoreForm,
      showPrunePreviewModal,
      pruneActionTitle,
      pruneExecuting,
      pruneResult,
      showRulePruneModal,
      rulePruneForm,
      formatBytes,
      formatTime,
      refreshAll,
      runTask,
      deleteTask,
      viewTaskSnapshots,
      openCreateTaskModal,
      openEditTaskModal,
      onCronPresetChange,
      fetchCronPreview,
      saveTask,
      openPickerFor,
      onPickerConfirm,
      toggleSelectAll,
      toggleSelectSnapshot,
      togglePin,
      previewManualBatchDelete,
      openRulePruneModal,
      previewRulePrune,
      previewGfsPrune,
      executeConfirmedPrune,
      deleteSingleSnapshot,
      openFileTree,
      openRestoreModal,
      executeRestore,
      getTaskName,
    };
  },
  template: `
    <div class="space-y-6">
      <!-- 页面标题与顶部操作栏 -->
      <div class="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold text-gray-900 dark:text-gray-100 flex items-center gap-2">
            <span>文件夹备份与同步</span>
            <span class="text-xs px-2.5 py-0.5 rounded-full font-medium bg-blue-100 dark:bg-blue-900/40 text-blue-800 dark:text-blue-300">
              增量硬链接 & GFS 治理
            </span>
          </h1>
          <p class="text-sm text-gray-500 dark:text-gray-400 mt-1">
            支持目录多版本增量快照、实时/定时直接镜像同步，提供秒级回溯与批量生命周期修剪
          </p>
        </div>
        <div class="flex items-center gap-3">
          <button
            type="button"
            class="px-3.5 py-2 rounded-lg text-sm font-medium border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 hover:bg-gray-50 dark:hover:bg-gray-700 text-gray-700 dark:text-gray-200 transition-colors shadow-sm"
            @click="refreshAll()"
          >
            刷新
          </button>
          <button
            type="button"
            class="px-4 py-2 rounded-lg text-sm font-medium bg-blue-600 hover:bg-blue-700 text-white transition-colors shadow-sm flex items-center gap-1.5"
            @click="openCreateTaskModal()"
          >
            <span>+ 新建任务</span>
          </button>
        </div>
      </div>

      <!-- 核心指标卡片 -->
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <div class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <div class="text-xs font-medium text-gray-500 dark:text-gray-400">全部任务</div>
          <div class="mt-2 text-2xl font-semibold text-gray-900 dark:text-gray-100">{{ tasks.length }}</div>
        </div>
        <div class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <div class="text-xs font-medium text-blue-600 dark:text-blue-400">增量备份任务</div>
          <div class="mt-2 text-2xl font-semibold text-gray-900 dark:text-gray-100">{{ backupTasks.length }}</div>
        </div>
        <div class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <div class="text-xs font-medium text-emerald-600 dark:text-emerald-400">镜像同步任务</div>
          <div class="mt-2 text-2xl font-semibold text-gray-900 dark:text-gray-100">{{ syncTasks.length }}</div>
        </div>
        <div class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <div class="text-xs font-medium text-purple-600 dark:text-purple-400">历史快照总计</div>
          <div class="mt-2 text-2xl font-semibold text-gray-900 dark:text-gray-100">{{ snapshots.length }}</div>
        </div>
      </div>

      <!-- 标签页导航 (Tabs) -->
      <div class="border-b border-gray-200 dark:border-gray-700">
        <nav class="flex space-x-6">
          <button
            type="button"
            class="py-3 px-1 border-b-2 font-medium text-sm transition-colors"
            :class="activeTab === 'backup' ? 'border-blue-500 text-blue-600 dark:text-blue-400' : 'border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300'"
            @click="activeTab = 'backup'"
          >
            增量备份任务 ({{ backupTasks.length }})
          </button>
          <button
            type="button"
            class="py-3 px-1 border-b-2 font-medium text-sm transition-colors"
            :class="activeTab === 'sync' ? 'border-blue-500 text-blue-600 dark:text-blue-400' : 'border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300'"
            @click="activeTab = 'sync'"
          >
            直接同步任务 ({{ syncTasks.length }})
          </button>
          <button
            type="button"
            class="py-3 px-1 border-b-2 font-medium text-sm transition-colors"
            :class="activeTab === 'snapshots' ? 'border-blue-500 text-blue-600 dark:text-blue-400' : 'border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300'"
            @click="activeTab = 'snapshots'"
          >
            快照库与恢复 ({{ snapshots.length }})
          </button>
        </nav>
      </div>

      <!-- Tab 1: 增量备份任务列表 -->
      <div v-if="activeTab === 'backup'" class="space-y-4">
        <div v-if="backupTasks.length === 0" class="p-8 text-center bg-white dark:bg-gray-850 rounded-xl border border-gray-200 dark:border-gray-800 text-gray-500">
          暂无增量备份任务，点击右上角“+ 新建任务”创建首个版本备份作业。
        </div>
        <div v-else class="overflow-x-auto rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <table class="w-full text-left text-sm">
            <thead class="bg-gray-50 dark:bg-gray-800 text-gray-600 dark:text-gray-300 font-medium border-b border-gray-200 dark:border-gray-700">
              <tr>
                <th class="p-4">任务名称</th>
                <th class="p-4">路径 (源端 -> 备份库)</th>
                <th class="p-4">调度频率</th>
                <th class="p-4">GFS 保留策略</th>
                <th class="p-4">上次运行</th>
                <th class="p-4 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-100 dark:divide-gray-800">
              <tr v-for="t in backupTasks" :key="t.id" class="hover:bg-gray-50/50 dark:hover:bg-gray-800/50 transition-colors">
                <td class="p-4 font-medium text-gray-900 dark:text-gray-100">
                  <div class="flex items-center gap-2">
                    <span>{{ t.name }}</span>
                    <span v-if="!t.enabled" class="text-xs px-2 py-0.5 rounded bg-gray-100 dark:bg-gray-700 text-gray-500">停用</span>
                  </div>
                </td>
                <td class="p-4 text-gray-600 dark:text-gray-400">
                  <div class="font-mono text-xs">{{ t.sourcePath }}</div>
                  <div class="text-xs text-gray-400 mt-0.5">↳ {{ t.targetPath }}</div>
                </td>
                <td class="p-4">
                  <span class="inline-block px-2 py-1 rounded text-xs font-mono bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300">
                    {{ t.cronDescription || t.cronExpression }}
                  </span>
                </td>
                <td class="p-4 text-xs text-gray-600 dark:text-gray-400">
                  <span>最近{{ t.keepLastCount }}次</span> · 
                  <span>{{ t.keepDays }}天</span> · 
                  <span>{{ t.keepWeeks }}周</span> · 
                  <span>{{ t.keepMonths }}月</span>
                </td>
                <td class="p-4 text-xs">
                  <div :class="t.lastRunStatus === 'Success' ? 'text-emerald-600 dark:text-emerald-400' : (t.lastRunStatus === 'Failed' ? 'text-red-600 dark:text-red-400' : 'text-gray-500')">
                    {{ t.lastRunStatus || '未运行' }}
                  </div>
                  <div class="text-gray-400 mt-0.5">{{ formatTime(t.lastRunTime) }}</div>
                </td>
                <td class="p-4 text-right space-x-2">
                  <button type="button" class="text-xs text-blue-600 hover:text-blue-800 font-medium" @click="runTask(t)">立即备份</button>
                  <button type="button" class="text-xs text-purple-600 hover:text-purple-800 font-medium" @click="viewTaskSnapshots(t)">查看快照</button>
                  <button type="button" class="text-xs text-gray-600 hover:text-gray-900 dark:text-gray-300 font-medium" @click="openEditTaskModal(t)">编辑</button>
                  <button type="button" class="text-xs text-red-600 hover:text-red-800 font-medium" @click="deleteTask(t)">删除</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 2: 直接同步任务列表 -->
      <div v-if="activeTab === 'sync'" class="space-y-4">
        <div v-if="syncTasks.length === 0" class="p-8 text-center bg-white dark:bg-gray-850 rounded-xl border border-gray-200 dark:border-gray-800 text-gray-500">
          暂无直接镜像同步任务，点击右上角“+ 新建任务”创建首个目录镜像作业。
        </div>
        <div v-else class="overflow-x-auto rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <table class="w-full text-left text-sm">
            <thead class="bg-gray-50 dark:bg-gray-800 text-gray-600 dark:text-gray-300 font-medium border-b border-gray-200 dark:border-gray-700">
              <tr>
                <th class="p-4">任务名称</th>
                <th class="p-4">路径 (源端 -> 镜像目标)</th>
                <th class="p-4">同步策略</th>
                <th class="p-4">调度频率</th>
                <th class="p-4">上次同步</th>
                <th class="p-4 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-100 dark:divide-gray-800">
              <tr v-for="t in syncTasks" :key="t.id" class="hover:bg-gray-50/50 dark:hover:bg-gray-800/50 transition-colors">
                <td class="p-4 font-medium text-gray-900 dark:text-gray-100">
                  <div class="flex items-center gap-2">
                    <span>{{ t.name }}</span>
                    <span v-if="!t.enabled" class="text-xs px-2 py-0.5 rounded bg-gray-100 dark:bg-gray-700 text-gray-500">停用</span>
                  </div>
                </td>
                <td class="p-4 text-gray-600 dark:text-gray-400">
                  <div class="font-mono text-xs">{{ t.sourcePath }}</div>
                  <div class="text-xs text-gray-400 mt-0.5">↳ {{ t.targetPath }}</div>
                </td>
                <td class="p-4 text-xs text-gray-600 dark:text-gray-400">
                  <div>{{ t.syncDirection === 1 ? '单向镜像 (跟随删除)' : '单向累加 (不删目标)' }}</div>
                  <div v-if="t.syncDirection === 1" class="text-gray-400 mt-0.5">
                    {{ t.deleteHandling === 2 ? '暂存目标回收站 (' + t.trashRetentionDays + '天)' : '直接永久删除' }}
                  </div>
                </td>
                <td class="p-4">
                  <span class="inline-block px-2 py-1 rounded text-xs font-mono bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300">
                    {{ t.cronDescription || t.cronExpression }}
                  </span>
                </td>
                <td class="p-4 text-xs">
                  <div :class="t.lastRunStatus === 'Success' ? 'text-emerald-600 dark:text-emerald-400' : (t.lastRunStatus === 'Failed' ? 'text-red-600 dark:text-red-400' : 'text-gray-500')">
                    {{ t.lastRunStatus || '未运行' }}
                  </div>
                  <div class="text-gray-400 mt-0.5">{{ formatTime(t.lastRunTime) }}</div>
                </td>
                <td class="p-4 text-right space-x-2">
                  <button type="button" class="text-xs text-blue-600 hover:text-blue-800 font-medium" @click="runTask(t)">立即同步</button>
                  <button type="button" class="text-xs text-gray-600 hover:text-gray-900 dark:text-gray-300 font-medium" @click="openEditTaskModal(t)">编辑</button>
                  <button type="button" class="text-xs text-red-600 hover:text-red-800 font-medium" @click="deleteTask(t)">删除</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 3: 快照库与恢复 (Snapshots & Restore) -->
      <div v-if="activeTab === 'snapshots'" class="space-y-4">
        <!-- 筛选与批量操作工具栏 -->
        <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <div class="flex items-center gap-3">
            <span class="text-sm font-medium text-gray-700 dark:text-gray-300">归属任务:</span>
            <select
              v-model="selectedTaskId"
              class="text-sm rounded-lg border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 px-3 py-1.5 text-gray-900 dark:text-gray-100"
            >
              <option value="">全部备份任务 ({{ snapshots.length }})</option>
              <option v-for="t in backupTasks" :key="t.id" :value="t.id">
                {{ t.name }}
              </option>
            </select>
          </div>

          <!-- 批量清理操作按钮组 -->
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="px-3 py-1.5 rounded-lg text-xs font-medium border border-red-300 dark:border-red-800/60 bg-red-50 dark:bg-red-950/40 text-red-700 dark:text-red-300 hover:bg-red-100 dark:hover:bg-red-900/60 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              :disabled="selectedSnapshotIds.length === 0"
              @click="previewManualBatchDelete()"
            >
              批量删除 (已选 {{ selectedSnapshotIds.length }})
            </button>
            <button
              type="button"
              class="px-3 py-1.5 rounded-lg text-xs font-medium border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
              @click="openRulePruneModal()"
            >
              条件规则清理
            </button>
            <button
              type="button"
              class="px-3 py-1.5 rounded-lg text-xs font-medium border border-purple-300 dark:border-purple-800 bg-purple-50 dark:bg-purple-950/40 text-purple-700 dark:text-purple-300 hover:bg-purple-100 dark:hover:bg-purple-900/60 transition-colors"
              @click="previewGfsPrune()"
            >
              GFS 一键修剪
            </button>
          </div>
        </div>

        <div v-if="filteredSnapshots.length === 0" class="p-8 text-center bg-white dark:bg-gray-850 rounded-xl border border-gray-200 dark:border-gray-800 text-gray-500">
          暂无快照数据。当增量备份任务执行后，生成的独立时间线快照将呈现在此。
        </div>

        <div v-else class="overflow-x-auto rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-850 shadow-sm">
          <table class="w-full text-left text-sm">
            <thead class="bg-gray-50 dark:bg-gray-800 text-gray-600 dark:text-gray-300 font-medium border-b border-gray-200 dark:border-gray-700">
              <tr>
                <th class="p-4 w-10">
                  <input
                    type="checkbox"
                    :checked="isAllSelected"
                    class="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    @change="toggleSelectAll()"
                  />
                </th>
                <th class="p-4">快照编号 (时间戳)</th>
                <th class="p-4">归属任务</th>
                <th class="p-4">归档标记</th>
                <th class="p-4">锁状态 (误删防线)</th>
                <th class="p-4">文件数 / 容量</th>
                <th class="p-4 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-100 dark:divide-gray-800">
              <tr v-for="s in filteredSnapshots" :key="s.snapshotId" class="hover:bg-gray-50/50 dark:hover:bg-gray-800/50 transition-colors">
                <td class="p-4">
                  <input
                    type="checkbox"
                    :checked="selectedSnapshotIds.includes(s.snapshotId)"
                    class="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    @change="toggleSelectSnapshot(s.snapshotId)"
                  />
                </td>
                <td class="p-4 font-mono font-medium text-gray-900 dark:text-gray-100">
                  <div>{{ s.snapshotId }}</div>
                  <div class="text-xs text-gray-400 mt-0.5">{{ formatTime(s.createdAt) }}</div>
                </td>
                <td class="p-4 text-xs text-gray-600 dark:text-gray-400">
                  {{ getTaskName(s.taskId) }}
                </td>
                <td class="p-4">
                  <span
                    class="inline-block px-2 py-0.5 rounded text-xs font-medium"
                    :class="s.tag === 'Latest' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900/50 dark:text-blue-300' : (s.tag === 'Daily' ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/50 dark:text-emerald-300' : (s.tag === 'Weekly' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900/50 dark:text-purple-300' : 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300'))"
                  >
                    {{ s.tag || '普通' }}
                  </span>
                </td>
                <td class="p-4">
                  <button
                    type="button"
                    class="px-2.5 py-1 rounded text-xs font-medium transition-colors flex items-center gap-1"
                    :class="s.isPinned ? 'bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300 hover:bg-amber-200' : 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400 hover:bg-gray-200'"
                    @click="togglePin(s)"
                  >
                    <span>{{ s.isPinned ? '🔒 已锁定' : '🔓 未锁定' }}</span>
                  </button>
                </td>
                <td class="p-4 text-xs text-gray-600 dark:text-gray-400">
                  <div>{{ s.totalFiles }} 个文件</div>
                  <div class="font-mono mt-0.5">{{ formatBytes(s.totalSizeBytes) }}</div>
                </td>
                <td class="p-4 text-right space-x-2">
                  <button type="button" class="text-xs text-blue-600 hover:text-blue-800 font-medium" @click="openFileTree(s)">浏览文件</button>
                  <button type="button" class="text-xs text-emerald-600 hover:text-emerald-800 font-medium" @click="openRestoreModal(s)">还原</button>
                  <button
                    type="button"
                    class="text-xs font-medium"
                    :class="s.isPinned ? 'text-gray-400 cursor-not-allowed' : 'text-red-600 hover:text-red-800'"
                    :disabled="s.isPinned"
                    @click="deleteSingleSnapshot(s)"
                  >
                    删除
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 新建 / 编辑任务弹窗 -->
      <div v-if="showTaskModal" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
        <div class="bg-white dark:bg-gray-850 rounded-2xl max-w-2xl w-full p-6 space-y-5 border border-gray-200 dark:border-gray-800 shadow-xl max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-gray-100 dark:border-gray-800 pb-3">
            <h2 class="text-lg font-bold text-gray-900 dark:text-gray-100">
              {{ editingTaskId ? '编辑任务' : '新建备份与同步任务' }}
            </h2>
            <button type="button" class="text-gray-400 hover:text-gray-600" @click="showTaskModal = false">✕</button>
          </div>

          <div class="space-y-4">
            <!-- 模式选择 -->
            <div>
              <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">运作模式</label>
              <div class="grid grid-cols-2 gap-3">
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer transition-colors" :class="taskForm.mode === 2 ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
                  <input type="radio" v-model="taskForm.mode" :value="2" class="text-blue-600" />
                  <div>
                    <div class="text-sm font-medium text-gray-900 dark:text-gray-100">增量版本备份 (Backup)</div>
                    <div class="text-xs text-gray-500">硬链接多版本快照，GFS生命周期管理与历史回滚</div>
                  </div>
                </label>
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer transition-colors" :class="taskForm.mode === 1 ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
                  <input type="radio" v-model="taskForm.mode" :value="1" class="text-blue-600" />
                  <div>
                    <div class="text-sm font-medium text-gray-900 dark:text-gray-100">直接镜像同步 (Sync)</div>
                    <div class="text-xs text-gray-500">源目录最新状态镜像至目标端，支持删除暂存回收站</div>
                  </div>
                </label>
              </div>
            </div>

            <!-- 任务名称 -->
            <div>
              <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">任务名称</label>
              <input
                type="text"
                v-model="taskForm.name"
                placeholder="例如：开发代码每日增量备份"
                class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-2 text-gray-900 dark:text-gray-100"
              />
            </div>

            <!-- 源目录与目标目录 -->
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div>
                <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">源端目录 (Source)</label>
                <div class="flex gap-2">
                  <input
                    type="text"
                    v-model="taskForm.sourcePath"
                    placeholder="/data/projects"
                    class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-2 font-mono text-xs text-gray-900 dark:text-gray-100"
                  />
                  <button
                    type="button"
                    class="px-3 py-2 rounded-lg text-xs font-medium border border-gray-300 dark:border-gray-600 bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 shrink-0"
                    @click="openPickerFor('sourcePath')"
                  >
                    选择
                  </button>
                </div>
              </div>

              <div>
                <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">目标目录 (Target)</label>
                <div class="flex gap-2">
                  <input
                    type="text"
                    v-model="taskForm.targetPath"
                    placeholder="/backup/projects"
                    class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-2 font-mono text-xs text-gray-900 dark:text-gray-100"
                  />
                  <button
                    type="button"
                    class="px-3 py-2 rounded-lg text-xs font-medium border border-gray-300 dark:border-gray-600 bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 shrink-0"
                    @click="openPickerFor('targetPath')"
                  >
                    选择
                  </button>
                </div>
              </div>
            </div>

            <!-- 定时调度 (Cron) -->
            <div class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-gray-50/50 dark:bg-gray-800/40 space-y-3">
              <div class="flex items-center justify-between">
                <label class="text-xs font-medium text-gray-700 dark:text-gray-300">定时触发设置</label>
                <div class="flex gap-2">
                  <select
                    v-model="taskForm.cronPreset"
                    class="text-xs rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 px-2 py-1 text-gray-900 dark:text-gray-100"
                    @change="onCronPresetChange()"
                  >
                    <option value="daily_02">每天凌晨 02:00</option>
                    <option value="hourly">每小时一次</option>
                    <option value="weekly_sun">每周日凌晨 03:00</option>
                    <option value="monthly_1st">每月 1 号凌晨 04:00</option>
                    <option value="custom">高级自定义 Cron</option>
                  </select>
                </div>
              </div>

              <div class="flex gap-2">
                <input
                  type="text"
                  v-model="taskForm.cronExpression"
                  class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-1.5 font-mono text-xs text-gray-900 dark:text-gray-100"
                  @input="fetchCronPreview()"
                />
                <button
                  type="button"
                  class="px-3 py-1.5 rounded-lg text-xs font-medium border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 hover:bg-gray-50 shrink-0"
                  @click="fetchCronPreview()"
                >
                  测试预测
                </button>
              </div>

              <div v-if="cronPreviewError" class="text-xs text-red-500 font-mono">{{ cronPreviewError }}</div>
              <div v-else-if="cronPreviewList.length > 0" class="text-xs text-gray-500 space-y-0.5">
                <span class="font-medium text-gray-700 dark:text-gray-300">预计未来触发节点：</span>
                <div class="grid grid-cols-2 gap-1 font-mono text-xs text-blue-600 dark:text-blue-400">
                  <div v-for="(pTime, idx) in cronPreviewList" :key="idx">
                    {{ idx + 1 }}. {{ formatTime(pTime) }}
                  </div>
                </div>
              </div>
            </div>

            <!-- 增量备份专属设置 (GFS 轮转) -->
            <div v-if="taskForm.mode === 2" class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-gray-50/50 dark:bg-gray-800/40 space-y-3">
              <div class="text-xs font-medium text-gray-700 dark:text-gray-300">GFS 多维快照保留轮转策略</div>
              <div class="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">保留最近连续次数</label>
                  <input type="number" min="1" v-model.number="taskForm.keepLastCount" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100" />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">每日快照天数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepDays" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100" />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">每周快照周数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepWeeks" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100" />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">每月快照月数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepMonths" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100" />
                </div>
              </div>
            </div>

            <!-- 直接同步专属设置 -->
            <div v-if="taskForm.mode === 1" class="p-4 rounded-xl border border-gray-200 dark:border-gray-800 bg-gray-50/50 dark:bg-gray-800/40 space-y-3">
              <div class="text-xs font-medium text-gray-700 dark:text-gray-300">同步模式专属设置</div>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">同步方向</label>
                  <select v-model="taskForm.syncDirection" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100">
                    <option :value="1">单向镜像 (源端删除时目标跟随删除)</option>
                    <option :value="2">单向累加 (源端删除时目标端保留)</option>
                  </select>
                </div>
                <div v-if="taskForm.syncDirection === 1">
                  <label class="block text-xs text-gray-500 mb-1">删除行为</label>
                  <select v-model="taskForm.deleteHandling" class="w-full text-xs rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 p-1.5 text-gray-900 dark:text-gray-100">
                    <option :value="2">暂存目标回收站 (.lwt_trash)</option>
                    <option :value="1">直接永久删除目标多余文件</option>
                  </select>
                </div>
              </div>
            </div>

            <!-- 排除规则 -->
            <div>
              <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">排除规则 (通配符以分号或逗号分隔)</label>
              <input
                type="text"
                v-model="taskForm.excludePatternsText"
                placeholder="例如：node_modules; *.tmp; .git; *.cache"
                class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-2 font-mono text-xs text-gray-900 dark:text-gray-100"
              />
            </div>
          </div>

          <div class="flex items-center justify-end gap-3 pt-4 border-t border-gray-100 dark:border-gray-800">
            <button
              type="button"
              class="px-4 py-2 rounded-lg text-sm border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 hover:bg-gray-50 text-gray-700 dark:text-gray-200"
              @click="showTaskModal = false"
            >
              取消
            </button>
            <button
              type="button"
              class="px-5 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-700 text-white font-medium disabled:opacity-50"
              :disabled="taskSaving"
              @click="saveTask()"
            >
              {{ taskSaving ? '保存中...' : '确认保存' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 条件规则清理弹窗 -->
      <div v-if="showRulePruneModal" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
        <div class="bg-white dark:bg-gray-850 rounded-2xl max-w-md w-full p-6 space-y-4 border border-gray-200 dark:border-gray-800 shadow-xl">
          <div class="flex items-center justify-between border-b border-gray-100 dark:border-gray-800 pb-3">
            <h2 class="text-base font-bold text-gray-900 dark:text-gray-100">条件规则批量清理</h2>
            <button type="button" class="text-gray-400 hover:text-gray-600" @click="showRulePruneModal = false">✕</button>
          </div>

          <div class="space-y-3">
            <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="rulePruneForm.mode === 'olderThan' ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
              <input type="radio" v-model="rulePruneForm.mode" value="olderThan" class="text-blue-600" />
              <div class="flex items-center gap-2 text-sm">
                <span>清理早于</span>
                <input type="number" min="1" v-model.number="rulePruneForm.olderThanDays" class="w-16 p-1 text-center border rounded bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100" />
                <span>天的所有快照</span>
              </div>
            </label>

            <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="rulePruneForm.mode === 'keepLatest' ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
              <input type="radio" v-model="rulePruneForm.mode" value="keepLatest" class="text-blue-600" />
              <div class="flex items-center gap-2 text-sm">
                <span>仅保留最近</span>
                <input type="number" min="1" v-model.number="rulePruneForm.keepLatestCount" class="w-16 p-1 text-center border rounded bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100" />
                <span>份快照，批量清理其余</span>
              </div>
            </label>
            <p class="text-xs text-amber-600 dark:text-amber-400">
              * 安全底线保护：处于加锁保护 🔒 状态的快照与最新 1 份快照将被自动跳过。
            </p>
          </div>

          <div class="flex items-center justify-end gap-3 pt-3 border-t border-gray-100 dark:border-gray-800">
            <button type="button" class="px-3.5 py-1.5 rounded-lg text-sm border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-gray-700 dark:text-gray-200" @click="showRulePruneModal = false">取消</button>
            <button type="button" class="px-4 py-1.5 rounded-lg text-sm bg-blue-600 hover:bg-blue-700 text-white font-medium" @click="previewRulePrune()">开始预演</button>
          </div>
        </div>
      </div>

      <!-- 批量清理 / GFS 修剪 Dry-Run 预演确认弹窗 -->
      <div v-if="showPrunePreviewModal" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
        <div class="bg-white dark:bg-gray-850 rounded-2xl max-w-lg w-full p-6 space-y-4 border border-gray-200 dark:border-gray-800 shadow-xl">
          <div class="flex items-center justify-between border-b border-gray-100 dark:border-gray-800 pb-3">
            <h2 class="text-base font-bold text-gray-900 dark:text-gray-100">{{ pruneActionTitle }}</h2>
            <button type="button" class="text-gray-400 hover:text-gray-600" @click="showPrunePreviewModal = false">✕</button>
          </div>

          <div class="grid grid-cols-2 gap-3 p-3 rounded-xl bg-gray-50 dark:bg-gray-800/40 border border-gray-100 dark:border-gray-700">
            <div>
              <div class="text-xs text-gray-500">待清理快照数</div>
              <div class="text-lg font-bold text-red-600 dark:text-red-400">{{ pruneResult.deletedCount }} 份</div>
            </div>
            <div>
              <div class="text-xs text-gray-500">锁定保护跳过数</div>
              <div class="text-lg font-bold text-amber-600 dark:text-amber-400">{{ pruneResult.skippedPinnedCount }} 份</div>
            </div>
            <div class="col-span-2 pt-2 border-t border-gray-200 dark:border-gray-700">
              <div class="text-xs text-gray-500">预估释放物理空间</div>
              <div class="text-xl font-bold font-mono text-emerald-600 dark:text-emerald-400">
                {{ formatBytes(pruneResult.estimatedReclaimedBytes) }}
              </div>
            </div>
          </div>

          <div v-if="pruneResult.deletedCount === 0" class="p-3 text-sm text-gray-500 text-center">
            未发现符合修剪条件的快照（或快照均已被锁定/熔断保护）。
          </div>

          <div v-else class="space-y-2">
            <div class="text-xs font-medium text-gray-700 dark:text-gray-300">将受影响的快照清单：</div>
            <div class="max-h-36 overflow-y-auto p-2 rounded border border-gray-200 dark:border-gray-700 text-xs font-mono text-gray-600 dark:text-gray-400 space-y-1">
              <div v-for="sid in pruneResult.affectedSnapshotIds" :key="sid">
                • {{ sid }}
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-3 pt-3 border-t border-gray-100 dark:border-gray-800">
            <button
              type="button"
              class="px-4 py-2 rounded-lg text-sm border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-gray-700 dark:text-gray-200"
              @click="showPrunePreviewModal = false"
            >
              取消
            </button>
            <button
              type="button"
              class="px-5 py-2 rounded-lg text-sm bg-red-600 hover:bg-red-700 text-white font-medium disabled:opacity-40"
              :disabled="pruneResult.deletedCount === 0 || pruneExecuting"
              @click="executeConfirmedPrune()"
            >
              {{ pruneExecuting ? '正在清理中...' : '确认执行删除' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 快照目录树浏览弹窗 -->
      <div v-if="showFileTreeModal" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
        <div class="bg-white dark:bg-gray-850 rounded-2xl max-w-2xl w-full p-6 space-y-4 border border-gray-200 dark:border-gray-800 shadow-xl max-h-[85vh] flex flex-col">
          <div class="flex items-center justify-between border-b border-gray-100 dark:border-gray-800 pb-3">
            <div>
              <h2 class="text-base font-bold text-gray-900 dark:text-gray-100">
                快照文件浏览: {{ currentSnapshot?.snapshotId }}
              </h2>
              <div class="text-xs text-gray-400 mt-0.5">{{ currentSnapshot?.snapshotPath }}</div>
            </div>
            <button type="button" class="text-gray-400 hover:text-gray-600" @click="showFileTreeModal = false">✕</button>
          </div>

          <div v-if="fileTreeLoading" class="p-8 text-center text-sm text-gray-500">正在解析快照目录树...</div>
          <div v-else class="flex-1 overflow-y-auto space-y-1 font-mono text-xs">
            <div v-for="node in fileTreeNodes" :key="node.relativePath" class="p-1 hover:bg-gray-50 dark:hover:bg-gray-800 rounded flex items-center justify-between">
              <span class="flex items-center gap-1.5 text-gray-800 dark:text-gray-200">
                <span>{{ node.isDirectory ? '📁' : '📄' }}</span>
                <span>{{ node.relativePath }}</span>
              </span>
              <span v-if="!node.isDirectory" class="text-gray-400 text-2xs">{{ formatBytes(node.sizeBytes) }}</span>
            </div>
          </div>

          <div class="flex justify-end pt-3 border-t border-gray-100 dark:border-gray-800">
            <button type="button" class="px-4 py-1.5 rounded-lg text-sm border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-gray-700 dark:text-gray-200" @click="showFileTreeModal = false">关闭</button>
          </div>
        </div>
      </div>

      <!-- 快照还原确认弹窗 -->
      <div v-if="showRestoreModal" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
        <div class="bg-white dark:bg-gray-850 rounded-2xl max-w-lg w-full p-6 space-y-4 border border-gray-200 dark:border-gray-800 shadow-xl">
          <div class="flex items-center justify-between border-b border-gray-100 dark:border-gray-800 pb-3">
            <h2 class="text-base font-bold text-gray-900 dark:text-gray-100">快照还原作业</h2>
            <button type="button" class="text-gray-400 hover:text-gray-600" @click="showRestoreModal = false">✕</button>
          </div>

          <div class="space-y-3">
            <div>
              <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">还原模式</label>
              <div class="space-y-2">
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="restoreForm.restoreMode === 'source' ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
                  <input type="radio" v-model="restoreForm.restoreMode" value="source" class="text-blue-600" />
                  <div>
                    <div class="text-sm font-medium text-gray-900 dark:text-gray-100">原位覆盖回滚 (In-place Restore)</div>
                    <div class="text-xs text-gray-500">将快照数据直接写回原工作目录</div>
                  </div>
                </label>
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="restoreForm.restoreMode === 'custom' ? 'border-blue-500 bg-blue-50/40 dark:bg-blue-950/20' : 'border-gray-200 dark:border-gray-700'">
                  <input type="radio" v-model="restoreForm.restoreMode" value="custom" class="text-blue-600" />
                  <div>
                    <div class="text-sm font-medium text-gray-900 dark:text-gray-100">提取还原至指定新目录 (Custom Directory)</div>
                    <div class="text-xs text-gray-500">安全还原到单独目录进行校验，不影响当前生产文件</div>
                  </div>
                </label>
              </div>
            </div>

            <div v-if="restoreForm.restoreMode === 'custom'">
              <label class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">目标提取目录</label>
              <input
                type="text"
                v-model="restoreForm.customPath"
                placeholder="/restore/20261011_extracted"
                class="w-full text-sm rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-2 font-mono text-xs text-gray-900 dark:text-gray-100"
              />
            </div>

            <label class="flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300 cursor-pointer pt-1">
              <input type="checkbox" v-model="restoreForm.cleanTarget" class="rounded border-gray-300 text-blue-600" />
              <span>还原前彻底清空目标目录已有文件</span>
            </label>
          </div>

          <div class="flex items-center justify-end gap-3 pt-3 border-t border-gray-100 dark:border-gray-800">
            <button type="button" class="px-4 py-2 rounded-lg text-sm border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-gray-700 dark:text-gray-200" @click="showRestoreModal = false">取消</button>
            <button
              type="button"
              class="px-5 py-2 rounded-lg text-sm bg-emerald-600 hover:bg-emerald-700 text-white font-medium disabled:opacity-50"
              :disabled="restoreForm.restoring || (restoreForm.restoreMode === 'custom' && !restoreForm.customPath.trim())"
              @click="executeRestore()"
            >
              {{ restoreForm.restoring ? '正在还原中...' : '开始还原' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 全局文件目录选择器 -->
      <FilePicker
        :show="picker.show"
        :mode="picker.mode"
        :start-path="picker.startPath"
        @confirm="onPickerConfirm($event)"
        @cancel="picker.show = false"
      />
    </div>
  `,
});
