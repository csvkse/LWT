import { computed, defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
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

    // ---- 执行记录 ----
    const records = ref([]);
    const recordsLoading = ref(false);
    const recordFilterTaskId = ref('');
    const recordFilterStatus = ref('');
    const recordPage = ref(1);
    const recordPageSize = ref(20);
    const recordTotal = ref(0);
    const recordTotalPages = computed(() => Math.max(1, Math.ceil(recordTotal.value / recordPageSize.value)));

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

    const taskProgressMap = ref({});
    let progressTimer = null;
    let isProgressPolling = false;

    function formatSpeed(bytesPerSec) {
      if (!bytesPerSec || bytesPerSec <= 0) return '0 B/s';
      if (bytesPerSec >= 1024 * 1024 * 1024) return (bytesPerSec / (1024 * 1024 * 1024)).toFixed(1) + ' GB/s';
      if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / (1024 * 1024)).toFixed(1) + ' MB/s';
      if (bytesPerSec >= 1024) return (bytesPerSec / 1024).toFixed(1) + ' KB/s';
      return Math.round(bytesPerSec) + ' B/s';
    }

    function formatEta(ms) {
      if (!ms || ms <= 0) return '计算中...';
      const totalSec = Math.round(ms / 1000);
      if (totalSec < 60) return `${totalSec}秒`;
      const min = Math.floor(totalSec / 60);
      const sec = totalSec % 60;
      if (min < 60) return `${min}分${sec}秒`;
      const hr = Math.floor(min / 60);
      return `${hr}小时${min % 60}分`;
    }

    async function pollRunningTasksProgress() {
      if (isProgressPolling) return;
      isProgressPolling = true;
      try {
        const runningTasks = tasks.value.filter(t => t.lastRunStatus === 'Running' || taskProgressMap.value[t.id]?.isRunning);
        if (runningTasks.length === 0) return;

        for (const t of runningTasks) {
          try {
            const res = await http(API.backupSync.taskProgress(t.id));
            if (res.ok && res.data) {
              const prev = taskProgressMap.value[t.id];
              taskProgressMap.value[t.id] = res.data;
              if (prev && prev.isRunning && !res.data.isRunning) {
                await loadTasks();
                if (activeTab.value === 'snapshots') await loadAllSnapshots();
                if (activeTab.value === 'records') await loadRecords();
              }
            }
          } catch (_) {}
        }
      } finally {
        isProgressPolling = false;
      }
    }

    function startProgressPolling() {
      stopProgressPolling();
      progressTimer = setInterval(pollRunningTasksProgress, 1500);
    }

    function stopProgressPolling() {
      if (progressTimer) {
        clearInterval(progressTimer);
        progressTimer = null;
      }
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
      if (activeTab.value === 'records') {
        await loadRecords();
      }
    }

    async function loadRecords() {
      recordsLoading.value = true;
      try {
        const params = new URLSearchParams();
        if (recordFilterTaskId.value) params.set('taskId', recordFilterTaskId.value);
        if (recordFilterStatus.value) params.set('status', recordFilterStatus.value);
        params.set('page', recordPage.value.toString());
        params.set('pageSize', recordPageSize.value.toString());
        const res = await http(`${API.backupSync.records}?${params.toString()}`);
        if (res.ok && res.data) {
          records.value = res.data.items || [];
          recordTotal.value = res.data.total || 0;
        }
      } catch (e) {
        toast.error(`加载执行记录失败：${e.message}`);
      } finally {
        recordsLoading.value = false;
      }
    }

    function openRecordsTab(taskId = '') {
      activeTab.value = 'records';
      recordFilterTaskId.value = taskId;
      recordPage.value = 1;
      loadRecords();
    }

    async function clearRecords(taskId = null) {
      if (!confirm(taskId ? '确定清理该任务的历史执行记录吗？' : '确定清理全部执行记录吗？')) return;
      try {
        const res = await http(API.backupSync.clearRecords, {
          method: 'POST',
          body: JSON.stringify({ taskId: taskId || undefined, keepLatestCount: 10 })
        });
        if (res.ok) {
          toast.success(res.data?.message || '执行记录已清理');
          await loadRecords();
        }
      } catch (e) {
        toast.error(`清理记录失败：${e.message}`);
      }
    }

    // ---- 任务操作 ----
    async function runTask(task) {
      task.lastRunStatus = 'Running';
      taskProgressMap.value[task.id] = {
        isRunning: true,
        phaseDescription: '正在初始化任务...',
        filePercent: 0,
        bytePercent: 0,
        processedFiles: 0,
        scannedFiles: 0,
        processedBytes: 0,
        speedBytesPerSec: 0,
        currentItemPath: ''
      };
      setTimeout(pollRunningTasksProgress, 300);
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

    async function resumeTask(task) {
      try {
        const res = await http(API.backupSync.taskResume(task.id), { method: 'POST' });
        if (res.ok) {
          toast.success(`任务「${task.name}」已解除熔断，恢复定时调度`);
          await refreshAll();
        } else {
          toast.error(res.data?.message || '恢复调度失败');
        }
      } catch (e) {
        toast.error(`恢复调度异常：${e.message}`);
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
      startProgressPolling();
    });

    onUnmounted(() => {
      stopProgressPolling();
    });

    return {
      taskProgressMap,
      formatSpeed,
      formatEta,
      activeTab,
      loading,
      records,
      recordsLoading,
      recordFilterTaskId,
      recordFilterStatus,
      recordPage,
      recordPageSize,
      recordTotal,
      recordTotalPages,
      loadRecords,
      openRecordsTab,
      clearRecords,
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
      resumeTask,
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
    <div class="flex flex-col gap-4">
      <!-- 页面标题与顶部操作栏 -->
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="text-sm text-slate-400">
          文件夹备份与同步 <span class="text-slate-600">（增量硬链接 & GFS 治理 · 目录直接镜像同步）</span>
        </h2>
        <div class="ml-auto flex items-center gap-2">
          <button
            type="button"
            class="btn btn-xs"
            :disabled="loading"
            @click="refreshAll()"
          >
            {{ loading ? '刷新中…' : '刷新' }}
          </button>
          <button
            type="button"
            class="btn btn-primary"
            @click="openCreateTaskModal()"
          >
            ＋ 新建任务
          </button>
        </div>
      </div>

      <!-- 核心指标卡片 -->
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <div class="panel p-4">
          <div class="text-xs text-slate-400">全部任务</div>
          <div class="mt-1 text-2xl font-semibold text-slate-200 font-mono">{{ tasks.length }}</div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-cyan-300">增量备份任务</div>
          <div class="mt-1 text-2xl font-semibold text-slate-200 font-mono">{{ backupTasks.length }}</div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-emerald-400">镜像同步任务</div>
          <div class="mt-1 text-2xl font-semibold text-slate-200 font-mono">{{ syncTasks.length }}</div>
        </div>
        <div class="panel p-4">
          <div class="text-xs text-neon-soft">历史快照总计</div>
          <div class="mt-1 text-2xl font-semibold text-slate-200 font-mono">{{ snapshots.length }}</div>
        </div>
      </div>

      <!-- 标签页导航 (Tabs) -->
      <div class="flex border-b border-cyber-line gap-2">
        <button
          type="button"
          class="nav-link !rounded-b-none border-b-2"
          :class="activeTab === 'backup' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'backup'"
        >
          增量备份任务 ({{ backupTasks.length }})
        </button>
        <button
          type="button"
          class="nav-link !rounded-b-none border-b-2"
          :class="activeTab === 'sync' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'sync'"
        >
          直接同步任务 ({{ syncTasks.length }})
        </button>
        <button
          type="button"
          class="nav-link !rounded-b-none border-b-2"
          :class="activeTab === 'snapshots' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'snapshots'"
        >
          快照库与恢复 ({{ snapshots.length }})
        </button>
        <button
          type="button"
          class="nav-link !rounded-b-none border-b-2"
          :class="activeTab === 'records' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
          @click="openRecordsTab()"
        >
          执行记录
        </button>
      </div>

      <!-- Tab 1: 增量备份任务列表 -->
      <div v-if="activeTab === 'backup'" class="flex flex-col gap-3">
        <div v-if="backupTasks.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          暂无增量备份任务，点击右上角「＋ 新建任务」创建首个版本备份作业。
        </div>
        <div v-else class="panel overflow-x-auto">
          <table class="data-table min-w-[52rem]">
            <thead>
              <tr>
                <th>任务名称</th>
                <th>路径 (源端 -> 备份库)</th>
                <th>调度频率</th>
                <th>GFS 保留策略</th>
                <th>上次运行</th>
                <th class="text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="t in backupTasks" :key="t.id">
                <td class="font-medium text-slate-200">
                  <div class="flex items-center gap-2">
                    <span>{{ t.name }}</span>
                    <span v-if="!t.enabled" class="badge border-slate-600/60 text-slate-500">停用</span>
                  </div>
                  <!-- 实时执行进度面板 -->
                  <div v-if="taskProgressMap[t.id]?.isRunning || t.lastRunStatus === 'Running'" class="mt-2 p-2.5 rounded-lg bg-cyan-950/40 border border-cyan-500/30 text-xs shadow-inner min-w-[20rem]">
                    <div class="flex items-center justify-between gap-2 mb-1.5">
                      <div class="flex items-center gap-1.5 font-medium text-cyan-300">
                        <span class="inline-block w-2 h-2 rounded-full bg-cyan-400 animate-pulse"></span>
                        <span>{{ taskProgressMap[t.id]?.phaseDescription || '正在处理中...' }}</span>
                      </div>
                      <div class="font-mono text-cyan-400 font-bold text-xs">
                        {{ (taskProgressMap[t.id]?.filePercent || 0).toFixed(1) }}%
                      </div>
                    </div>
                    <!-- 动态渐变进度条 -->
                    <div class="w-full bg-slate-800/80 rounded-full h-1.5 overflow-hidden mb-1.5">
                      <div class="bg-gradient-to-r from-cyan-500 to-emerald-400 h-1.5 rounded-full transition-all duration-300"
                           :style="{ width: Math.max(3, (taskProgressMap[t.id]?.filePercent || 0)) + '%' }"></div>
                    </div>
                    <!-- 数据统计与指标 -->
                    <div class="flex items-center justify-between text-[11px] text-slate-400">
                      <div class="flex items-center gap-1.5">
                        <span>{{ (taskProgressMap[t.id]?.processedFiles || 0).toLocaleString() }} / {{ Math.max(taskProgressMap[t.id]?.processedFiles || 0, taskProgressMap[t.id]?.scannedFiles || 0).toLocaleString() }} 项</span>
                        <span>·</span>
                        <span>{{ formatBytes(taskProgressMap[t.id]?.processedBytes || 0) }}</span>
                      </div>
                      <div class="flex items-center gap-2 font-mono text-[11px]">
                        <span v-if="taskProgressMap[t.id]?.speedBytesPerSec > 0" class="text-cyan-300">
                          ⚡ {{ formatSpeed(taskProgressMap[t.id]?.speedBytesPerSec) }}
                        </span>
                        <span v-if="taskProgressMap[t.id]?.estimatedRemainingMs > 0" class="text-amber-300">
                          ⏳ 约 {{ formatEta(taskProgressMap[t.id]?.estimatedRemainingMs) }}
                        </span>
                      </div>
                    </div>
                    <!-- 实时当前文件追踪 -->
                    <div v-if="taskProgressMap[t.id]?.currentItemPath" class="text-[10px] text-slate-400 truncate mt-1 font-mono max-w-[28rem]" :title="taskProgressMap[t.id]?.currentItemPath">
                      正在处理: {{ taskProgressMap[t.id]?.currentItemPath }}
                    </div>
                  </div>
                </td>
                <td class="text-slate-400">
                  <div class="font-mono text-xs text-cyan-300/80">{{ t.sourcePath }}</div>
                  <div class="text-xs text-slate-500 mt-0.5">↳ {{ t.targetPath }}</div>
                </td>
                <td>
                  <span class="badge border-slate-600/60 text-slate-300 font-mono">
                    {{ t.cronDescription || t.cronExpression }}
                  </span>
                </td>
                <td class="text-xs text-slate-400">
                  <span>最近{{ t.keepLastCount }}次</span> · 
                  <span>{{ t.keepDays }}天</span> · 
                  <span>{{ t.keepWeeks }}周</span> · 
                  <span>{{ t.keepMonths }}月</span>
                </td>
                <td class="text-xs">
                  <div v-if="t.lastRunStatus === 'AutoPaused'" class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded border border-amber-500/50 bg-amber-500/10 text-amber-300 font-medium" :title="t.autoPausedReason || t.lastRunMessage">
                    <span>⚠ 自动暂停</span>
                  </div>
                  <div v-else-if="t.lastRunStatus === 'Running' || taskProgressMap[t.id]?.isRunning" class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded border border-cyan-500/50 bg-cyan-500/10 text-cyan-300 font-medium animate-pulse">
                    <span>⚡ {{ taskProgressMap[t.id]?.phaseDescription || '执行中...' }}</span>
                  </div>
                  <div v-else :class="t.lastRunStatus === 'Success' ? 'text-emerald-400' : (t.lastRunStatus === 'Failed' ? 'text-rose-400' : 'text-slate-500')">
                    {{ t.lastRunStatus === 'Success' ? '成功' : (t.lastRunStatus === 'Failed' ? '失败' : (t.lastRunStatus || '未运行')) }}
                  </div>
                  <div v-if="t.autoPausedReason" class="text-amber-400/90 text-[11px] mt-0.5 truncate max-w-[200px]" :title="t.autoPausedReason">
                    {{ t.autoPausedReason }}
                  </div>
                  <div v-else class="text-slate-500 mt-0.5 text-[11px]">{{ formatTime(t.lastRunTime) }}</div>
                </td>
                <td class="text-right whitespace-nowrap">
                  <div class="inline-flex items-center gap-1.5">
                    <button v-if="t.lastRunStatus === 'AutoPaused'" type="button" class="btn btn-xs border-amber-500/50 text-amber-300 hover:bg-amber-500/20" @click="resumeTask(t)" title="解除熔断状态并重新唤醒定时调度">恢复调度</button>
                    <button type="button" class="btn btn-xs btn-primary" @click="runTask(t)">立即备份</button>
                    <button type="button" class="btn btn-xs" @click="viewTaskSnapshots(t)">查看快照</button>
                    <button type="button" class="btn btn-xs" @click="openRecordsTab(t.id)">记录</button>
                    <button type="button" class="btn btn-xs" @click="openEditTaskModal(t)">编辑</button>
                    <button type="button" class="btn btn-xs btn-danger" @click="deleteTask(t)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 2: 直接同步任务列表 -->
      <div v-if="activeTab === 'sync'" class="flex flex-col gap-3">
        <div v-if="syncTasks.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          暂无直接镜像同步任务，点击右上角「＋ 新建任务」创建首个目录镜像作业。
        </div>
        <div v-else class="panel overflow-x-auto">
          <table class="data-table min-w-[52rem]">
            <thead>
              <tr>
                <th>任务名称</th>
                <th>路径 (源端 -> 镜像目标)</th>
                <th>同步策略</th>
                <th>调度频率</th>
                <th>上次同步</th>
                <th class="text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="t in syncTasks" :key="t.id">
                <td class="font-medium text-slate-200">
                  <div class="flex items-center gap-2">
                    <span>{{ t.name }}</span>
                    <span v-if="!t.enabled" class="badge border-slate-600/60 text-slate-500">停用</span>
                  </div>
                  <!-- 实时执行进度面板 -->
                  <div v-if="taskProgressMap[t.id]?.isRunning || t.lastRunStatus === 'Running'" class="mt-2 p-2.5 rounded-lg bg-cyan-950/40 border border-cyan-500/30 text-xs shadow-inner min-w-[20rem]">
                    <div class="flex items-center justify-between gap-2 mb-1.5">
                      <div class="flex items-center gap-1.5 font-medium text-cyan-300">
                        <span class="inline-block w-2 h-2 rounded-full bg-cyan-400 animate-pulse"></span>
                        <span>{{ taskProgressMap[t.id]?.phaseDescription || '正在处理中...' }}</span>
                      </div>
                      <div class="font-mono text-cyan-400 font-bold text-xs">
                        {{ (taskProgressMap[t.id]?.filePercent || 0).toFixed(1) }}%
                      </div>
                    </div>
                    <!-- 动态渐变进度条 -->
                    <div class="w-full bg-slate-800/80 rounded-full h-1.5 overflow-hidden mb-1.5">
                      <div class="bg-gradient-to-r from-cyan-500 to-emerald-400 h-1.5 rounded-full transition-all duration-300"
                           :style="{ width: Math.max(3, (taskProgressMap[t.id]?.filePercent || 0)) + '%' }"></div>
                    </div>
                    <!-- 数据统计与指标 -->
                    <div class="flex items-center justify-between text-[11px] text-slate-400">
                      <div class="flex items-center gap-1.5">
                        <span>{{ (taskProgressMap[t.id]?.processedFiles || 0).toLocaleString() }} / {{ Math.max(taskProgressMap[t.id]?.processedFiles || 0, taskProgressMap[t.id]?.scannedFiles || 0).toLocaleString() }} 项</span>
                        <span>·</span>
                        <span>{{ formatBytes(taskProgressMap[t.id]?.processedBytes || 0) }}</span>
                      </div>
                      <div class="flex items-center gap-2 font-mono text-[11px]">
                        <span v-if="taskProgressMap[t.id]?.speedBytesPerSec > 0" class="text-cyan-300">
                          ⚡ {{ formatSpeed(taskProgressMap[t.id]?.speedBytesPerSec) }}
                        </span>
                        <span v-if="taskProgressMap[t.id]?.estimatedRemainingMs > 0" class="text-amber-300">
                          ⏳ 约 {{ formatEta(taskProgressMap[t.id]?.estimatedRemainingMs) }}
                        </span>
                      </div>
                    </div>
                    <!-- 实时当前文件追踪 -->
                    <div v-if="taskProgressMap[t.id]?.currentItemPath" class="text-[10px] text-slate-400 truncate mt-1 font-mono max-w-[28rem]" :title="taskProgressMap[t.id]?.currentItemPath">
                      正在处理: {{ taskProgressMap[t.id]?.currentItemPath }}
                    </div>
                  </div>
                </td>
                <td class="text-slate-400">
                  <div class="font-mono text-xs text-cyan-300/80">{{ t.sourcePath }}</div>
                  <div class="text-xs text-slate-500 mt-0.5">↳ {{ t.targetPath }}</div>
                </td>
                <td class="text-xs text-slate-400">
                  <div>{{ t.syncDirection === 1 ? '单向镜像 (跟随删除)' : '单向累加 (不删目标)' }}</div>
                  <div v-if="t.syncDirection === 1" class="text-slate-500 mt-0.5">
                    {{ t.deleteHandling === 2 ? '暂存目标回收站 (' + t.trashRetentionDays + '天)' : '直接永久删除' }}
                  </div>
                </td>
                <td>
                  <span class="badge border-slate-600/60 text-slate-300 font-mono">
                    {{ t.cronDescription || t.cronExpression }}
                  </span>
                </td>
                <td class="text-xs">
                  <div v-if="t.lastRunStatus === 'AutoPaused'" class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded border border-amber-500/50 bg-amber-500/10 text-amber-300 font-medium" :title="t.autoPausedReason || t.lastRunMessage">
                    <span>⚠ 自动暂停</span>
                  </div>
                  <div v-else-if="t.lastRunStatus === 'Running' || taskProgressMap[t.id]?.isRunning" class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded border border-cyan-500/50 bg-cyan-500/10 text-cyan-300 font-medium animate-pulse">
                    <span>⚡ {{ taskProgressMap[t.id]?.phaseDescription || '执行中...' }}</span>
                  </div>
                  <div v-else :class="t.lastRunStatus === 'Success' ? 'text-emerald-400' : (t.lastRunStatus === 'Failed' ? 'text-rose-400' : 'text-slate-500')">
                    {{ t.lastRunStatus === 'Success' ? '成功' : (t.lastRunStatus === 'Failed' ? '失败' : (t.lastRunStatus || '未运行')) }}
                  </div>
                  <div v-if="t.autoPausedReason" class="text-amber-400/90 text-[11px] mt-0.5 truncate max-w-[200px]" :title="t.autoPausedReason">
                    {{ t.autoPausedReason }}
                  </div>
                  <div v-else class="text-slate-500 mt-0.5 text-[11px]">{{ formatTime(t.lastRunTime) }}</div>
                </td>
                <td class="text-right whitespace-nowrap">
                  <div class="inline-flex items-center gap-1.5">
                    <button v-if="t.lastRunStatus === 'AutoPaused'" type="button" class="btn btn-xs border-amber-500/50 text-amber-300 hover:bg-amber-500/20" @click="resumeTask(t)" title="解除熔断状态并重新唤醒定时调度">恢复调度</button>
                    <button type="button" class="btn btn-xs btn-primary" @click="runTask(t)">立即同步</button>
                    <button type="button" class="btn btn-xs" @click="openRecordsTab(t.id)">记录</button>
                    <button type="button" class="btn btn-xs" @click="openEditTaskModal(t)">编辑</button>
                    <button type="button" class="btn btn-xs btn-danger" @click="deleteTask(t)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 3: 快照库与恢复 (Snapshots & Restore) -->
      <div v-if="activeTab === 'snapshots'" class="flex flex-col gap-3">
        <!-- 筛选与批量操作工具栏 -->
        <div class="panel p-3 flex flex-wrap items-center justify-between gap-3">
          <div class="flex items-center gap-3">
            <span class="text-xs font-medium text-slate-400">归属任务:</span>
            <select
              v-model="selectedTaskId"
              class="input font-mono text-xs w-auto py-1"
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
              class="btn btn-xs btn-danger"
              :disabled="selectedSnapshotIds.length === 0"
              @click="previewManualBatchDelete()"
            >
              批量删除 (已选 {{ selectedSnapshotIds.length }})
            </button>
            <button
              type="button"
              class="btn btn-xs"
              @click="openRulePruneModal()"
            >
              条件规则清理
            </button>
            <button
              type="button"
              class="btn btn-xs btn-primary"
              @click="previewGfsPrune()"
            >
              GFS 一键修剪
            </button>
          </div>
        </div>

        <div v-if="filteredSnapshots.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          暂无快照数据。当增量备份任务执行后，生成的独立时间线快照将呈现在此。
        </div>

        <div v-else class="panel overflow-x-auto">
          <table class="data-table min-w-[56rem]">
            <thead>
              <tr>
                <th class="w-10">
                  <input
                    type="checkbox"
                    :checked="isAllSelected"
                    class="accent-cyan-400"
                    @change="toggleSelectAll()"
                  />
                </th>
                <th>快照编号 (时间戳)</th>
                <th>归属任务</th>
                <th>归档标记</th>
                <th>锁状态 (误删防线)</th>
                <th>文件数 / 容量</th>
                <th class="text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="s in filteredSnapshots" :key="s.snapshotId">
                <td>
                  <input
                    type="checkbox"
                    :checked="selectedSnapshotIds.includes(s.snapshotId)"
                    class="accent-cyan-400"
                    @change="toggleSelectSnapshot(s.snapshotId)"
                  />
                </td>
                <td class="font-mono font-medium text-slate-200">
                  <div class="text-cyan-300">{{ s.snapshotId }}</div>
                  <div class="text-xs text-slate-500 mt-0.5">{{ formatTime(s.createdAt) }}</div>
                </td>
                <td class="text-xs text-slate-400">
                  {{ getTaskName(s.taskId) }}
                </td>
                <td>
                  <span
                    class="badge"
                    :class="s.tag === 'Latest' ? 'border-cyan-500/50 text-cyan-300' : (s.tag === 'Daily' ? 'border-emerald-500/50 text-emerald-300' : (s.tag === 'Weekly' ? 'border-purple-500/50 text-purple-300' : 'border-slate-600/60 text-slate-400'))"
                  >
                    {{ s.tag || '普通' }}
                  </span>
                </td>
                <td>
                  <button
                    type="button"
                    class="btn btn-xs"
                    :class="s.isPinned ? '!border-amber-500/50 text-amber-300 bg-amber-500/10' : 'text-slate-400'"
                    @click="togglePin(s)"
                  >
                    <span>{{ s.isPinned ? '🔒 已锁定' : '🔓 未锁定' }}</span>
                  </button>
                </td>
                <td class="text-xs text-slate-400">
                  <div>{{ s.totalFiles }} 个文件</div>
                  <div class="font-mono mt-0.5 text-slate-300">{{ formatBytes(s.totalSizeBytes) }}</div>
                </td>
                <td class="text-right whitespace-nowrap">
                  <div class="inline-flex items-center gap-1.5">
                    <button type="button" class="btn btn-xs" @click="openFileTree(s)">浏览文件</button>
                    <button type="button" class="btn btn-xs btn-primary" @click="openRestoreModal(s)">还原</button>
                    <button
                      type="button"
                      class="btn btn-xs btn-danger"
                      :disabled="s.isPinned"
                      @click="deleteSingleSnapshot(s)"
                    >
                      删除
                    </button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Tab 4: 执行记录与历史审计 (Execution Logs) -->
      <div v-if="activeTab === 'records'" class="flex flex-col gap-3">
        <!-- 记录筛选工具栏 -->
        <div class="panel p-3 flex flex-wrap items-center justify-between gap-3">
          <div class="flex flex-wrap items-center gap-3">
            <span class="text-xs font-medium text-slate-400">过滤任务:</span>
            <select
              v-model="recordFilterTaskId"
              class="input font-mono text-xs w-auto py-1"
              @change="loadRecords()"
            >
              <option value="">全部任务</option>
              <option v-for="t in tasks" :key="t.id" :value="t.id">
                {{ t.name }} ({{ t.mode === 1 ? '直接同步' : '增量备份' }})
              </option>
            </select>

            <span class="text-xs font-medium text-slate-400">执行状态:</span>
            <select
              v-model="recordFilterStatus"
              class="input text-xs w-auto py-1"
              @change="loadRecords()"
            >
              <option value="">全部状态</option>
              <option value="Success">成功</option>
              <option value="Failed">失败</option>
              <option value="AutoPaused">自动暂停熔断</option>
              <option value="Running">执行中</option>
              <option value="Interrupted">服务中断</option>
            </select>
          </div>

          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn btn-xs"
              :disabled="recordsLoading"
              @click="loadRecords()"
            >
              {{ recordsLoading ? '刷新中...' : '刷新' }}
            </button>
            <button
              type="button"
              class="btn btn-xs btn-danger"
              @click="clearRecords(recordFilterTaskId)"
              title="按当前过滤保留最新10条，修剪清理历史记录"
            >
              清理历史
            </button>
          </div>
        </div>

        <!-- 记录表格 -->
        <div class="panel overflow-x-auto">
          <div v-if="records.length === 0" class="p-8 text-center text-slate-500 text-xs">
            {{ recordsLoading ? '正在加载执行记录...' : '暂无相关执行记录' }}
          </div>
          <table v-else class="data-table min-w-[56rem]">
            <thead>
              <tr>
                <th>任务 / 模式</th>
                <th>触发方式</th>
                <th>状态</th>
                <th>开始时间 / 耗时</th>
                <th>文件 / 传输统计</th>
                <th>运行摘要 / 错误提示</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="r in records" :key="r.id">
                <td class="font-medium text-slate-200">
                  <div>{{ r.taskName }}</div>
                  <div class="text-[11px] text-slate-500 mt-0.5">
                    {{ r.mode === 1 ? '直接同步' : '增量备份' }}
                  </div>
                </td>
                <td class="text-xs text-slate-400">
                  <span class="badge border-slate-600/60 text-slate-300">
                    {{ r.triggerType === 1 ? '手动触发' : '定时调度' }}
                  </span>
                </td>
                <td class="text-xs">
                  <span
                    class="badge font-medium"
                    :class="{
                      'border-emerald-500/50 bg-emerald-500/10 text-emerald-300': r.status === 'Success',
                      'border-rose-500/50 bg-rose-500/10 text-rose-300': r.status === 'Failed',
                      'border-amber-500/50 bg-amber-500/10 text-amber-300': r.status === 'AutoPaused',
                      'border-cyan-500/50 bg-cyan-500/10 text-cyan-300': r.status === 'Running',
                      'border-slate-500/50 bg-slate-500/10 text-slate-400': r.status === 'Interrupted'
                    }"
                  >
                    {{ r.status === 'Success' ? '成功' : (r.status === 'Failed' ? '失败' : (r.status === 'AutoPaused' ? '自动熔断' : (r.status === 'Running' ? '执行中' : (r.status === 'Interrupted' ? '服务中断' : r.status)))) }}
                  </span>
                </td>
                <td class="text-xs text-slate-400">
                  <div class="font-mono text-cyan-300/80">{{ formatTime(r.startTime) }}</div>
                  <div class="text-[11px] text-slate-500 mt-0.5">耗时 {{ r.durationMs }}ms</div>
                </td>
                <td class="text-xs text-slate-300 font-mono">
                  <div>{{ r.processedFiles }} 个文件 · {{ formatBytes(r.processedBytes) }}</div>
                  <div class="text-[11px] text-slate-500 mt-0.5">
                    <span v-if="r.addedFiles > 0" class="text-emerald-400">+{{ r.addedFiles }} </span>
                    <span v-if="r.updatedFiles > 0" class="text-cyan-400">~{{ r.updatedFiles }} </span>
                    <span v-if="r.deletedFiles > 0" class="text-rose-400">-{{ r.deletedFiles }} </span>
                    <span v-if="r.skippedFiles > 0" class="text-amber-400">跳过: {{ r.skippedFiles }}</span>
                  </div>
                </td>
                <td class="text-xs max-w-xs">
                  <div class="text-slate-300 truncate" :title="r.summary">{{ r.summary || '-' }}</div>
                  <div v-if="r.errorMessage" class="text-rose-400/90 text-[11px] mt-0.5 truncate" :title="r.errorMessage">
                    ⚠ {{ r.errorMessage }}
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <!-- 分页栏 -->
        <div v-if="recordTotal > 0" class="flex items-center justify-between px-2 text-xs text-slate-400">
          <div>共 {{ recordTotal }} 条记录，第 {{ recordPage }} / {{ recordTotalPages }} 页</div>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="btn btn-xs"
              :disabled="recordPage <= 1"
              @click="recordPage--; loadRecords()"
            >
              上一页
            </button>
            <button
              type="button"
              class="btn btn-xs"
              :disabled="recordPage >= recordTotalPages"
              @click="recordPage++; loadRecords()"
            >
              下一页
            </button>
          </div>
        </div>
      </div>

      <!-- 新建 / 编辑任务弹窗 -->
      <div v-if="showTaskModal" class="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
        <div class="panel w-full max-w-2xl p-5 max-h-[90vh] overflow-y-auto flex flex-col gap-4" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-cyber-line pb-3">
            <h3 class="font-display text-base text-neon-soft">
              {{ editingTaskId ? '编辑任务' : '新建备份与同步任务' }}
            </h3>
            <button type="button" class="btn btn-xs" @click="showTaskModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3">
            <!-- 模式选择 -->
            <div>
              <label class="block text-xs text-slate-400 mb-1">运作模式</label>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer transition-colors" :class="taskForm.mode === 2 ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
                  <input type="radio" v-model="taskForm.mode" :value="2" class="accent-cyan-400" />
                  <div>
                    <div class="text-xs font-semibold text-slate-200">增量版本备份 (Backup)</div>
                    <div class="text-[11px] text-slate-400 mt-0.5">硬链接多版本快照，GFS生命周期管理与历史回滚</div>
                  </div>
                </label>
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer transition-colors" :class="taskForm.mode === 1 ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
                  <input type="radio" v-model="taskForm.mode" :value="1" class="accent-cyan-400" />
                  <div>
                    <div class="text-xs font-semibold text-slate-200">直接镜像同步 (Sync)</div>
                    <div class="text-[11px] text-slate-400 mt-0.5">源目录最新状态镜像至目标端，支持删除暂存回收站</div>
                  </div>
                </label>
              </div>
            </div>

            <!-- 任务名称 -->
            <div>
              <label class="block text-xs text-slate-400 mb-1">任务名称</label>
              <input
                type="text"
                v-model="taskForm.name"
                placeholder="例如：开发代码每日增量备份"
                class="input text-xs"
              />
            </div>

            <!-- 源目录与目标目录 -->
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-slate-400 mb-1">源端目录 (Source)</label>
                <div class="flex gap-2">
                  <input
                    type="text"
                    v-model="taskForm.sourcePath"
                    placeholder="/data/projects"
                    class="input font-mono text-xs"
                  />
                  <button
                    type="button"
                    class="btn btn-xs shrink-0 self-center"
                    @click="openPickerFor('sourcePath')"
                  >
                    📂 浏览
                  </button>
                </div>
              </div>

              <div>
                <label class="block text-xs text-slate-400 mb-1">目标目录 (Target)</label>
                <div class="flex gap-2">
                  <input
                    type="text"
                    v-model="taskForm.targetPath"
                    placeholder="/backup/projects"
                    class="input font-mono text-xs"
                  />
                  <button
                    type="button"
                    class="btn btn-xs shrink-0 self-center"
                    @click="openPickerFor('targetPath')"
                  >
                    📂 浏览
                  </button>
                </div>
              </div>
            </div>

            <!-- 定时调度 (Cron) -->
            <div class="panel p-3 flex flex-col gap-2">
              <div class="flex items-center justify-between">
                <label class="text-xs text-slate-400">定时触发设置</label>
                <select
                  v-model="taskForm.cronPreset"
                  class="input text-xs w-auto py-1"
                  @change="onCronPresetChange()"
                >
                  <option value="daily_02">每天凌晨 02:00</option>
                  <option value="hourly">每小时一次</option>
                  <option value="weekly_sun">每周日凌晨 03:00</option>
                  <option value="monthly_1st">每月 1 号凌晨 04:00</option>
                  <option value="custom">高级自定义 Cron</option>
                </select>
              </div>

              <div class="flex gap-2">
                <input
                  type="text"
                  v-model="taskForm.cronExpression"
                  class="input font-mono text-xs flex-1"
                  @input="fetchCronPreview()"
                />
                <button
                  type="button"
                  class="btn btn-xs"
                  @click="fetchCronPreview()"
                >
                  测试预测
                </button>
              </div>

              <div v-if="cronPreviewError" class="text-xs text-rose-400 font-mono">{{ cronPreviewError }}</div>
              <div v-else-if="cronPreviewList.length > 0" class="text-xs text-slate-400 space-y-0.5">
                <span class="text-slate-400">预计未来触发节点：</span>
                <div class="grid grid-cols-2 gap-1 font-mono text-xs text-cyan-300">
                  <div v-for="(pTime, idx) in cronPreviewList" :key="idx">
                    {{ idx + 1 }}. {{ formatTime(pTime) }}
                  </div>
                </div>
              </div>
            </div>

            <!-- 增量备份专属设置 (GFS 轮转) -->
            <div v-if="taskForm.mode === 2" class="panel p-3 flex flex-col gap-2">
              <div class="text-xs text-slate-400">GFS 多维快照保留轮转策略</div>
              <div class="grid grid-cols-2 sm:grid-cols-4 gap-2">
                <div>
                  <label class="block text-[11px] text-slate-500 mb-1">保留最近连续次数</label>
                  <input type="number" min="1" v-model.number="taskForm.keepLastCount" class="input font-mono text-xs" />
                </div>
                <div>
                  <label class="block text-[11px] text-slate-500 mb-1">每日快照天数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepDays" class="input font-mono text-xs" />
                </div>
                <div>
                  <label class="block text-[11px] text-slate-500 mb-1">每周快照周数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepWeeks" class="input font-mono text-xs" />
                </div>
                <div>
                  <label class="block text-[11px] text-slate-500 mb-1">每月快照月数</label>
                  <input type="number" min="0" v-model.number="taskForm.keepMonths" class="input font-mono text-xs" />
                </div>
              </div>
            </div>

            <!-- 直接同步专属设置 -->
            <div v-if="taskForm.mode === 1" class="panel p-3 flex flex-col gap-2">
              <div class="text-xs text-slate-400">同步模式专属设置</div>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label class="block text-[11px] text-slate-500 mb-1">同步方向</label>
                  <select v-model="taskForm.syncDirection" class="input text-xs">
                    <option :value="1">单向镜像 (源端删除时目标跟随删除)</option>
                    <option :value="2">单向累加 (源端删除时目标端保留)</option>
                  </select>
                </div>
                <div v-if="taskForm.syncDirection === 1">
                  <label class="block text-[11px] text-slate-500 mb-1">删除行为</label>
                  <select v-model="taskForm.deleteHandling" class="input text-xs">
                    <option :value="2">暂存目标回收站 (.lwt_trash)</option>
                    <option :value="1">直接永久删除目标多余文件</option>
                  </select>
                </div>
              </div>
            </div>

            <!-- 排除规则 -->
            <div>
              <label class="block text-xs text-slate-400 mb-1">排除规则 (通配符以分号或逗号分隔)</label>
              <input
                type="text"
                v-model="taskForm.excludePatternsText"
                placeholder="例如：node_modules; *.tmp; .git; *.cache"
                class="input font-mono text-xs"
              />
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 pt-3 border-t border-cyber-line">
            <button
              type="button"
              class="btn"
              @click="showTaskModal = false"
            >
              取消
            </button>
            <button
              type="button"
              class="btn btn-primary"
              :disabled="taskSaving"
              @click="saveTask()"
            >
              {{ taskSaving ? '保存中...' : '确认保存' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 条件规则清理弹窗 -->
      <div v-if="showRulePruneModal" class="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
        <div class="panel w-full max-w-md p-5 flex flex-col gap-4" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-cyber-line pb-3">
            <h3 class="font-display text-base text-neon-soft">条件规则批量清理</h3>
            <button type="button" class="btn btn-xs" @click="showRulePruneModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3">
            <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="rulePruneForm.mode === 'olderThan' ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
              <input type="radio" v-model="rulePruneForm.mode" value="olderThan" class="accent-cyan-400" />
              <div class="flex items-center gap-2 text-xs">
                <span>清理早于</span>
                <input type="number" min="1" v-model.number="rulePruneForm.olderThanDays" class="input font-mono text-xs w-16 text-center py-0.5 inline-block" />
                <span>天的所有快照</span>
              </div>
            </label>

            <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="rulePruneForm.mode === 'keepLatest' ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
              <input type="radio" v-model="rulePruneForm.mode" value="keepLatest" class="accent-cyan-400" />
              <div class="flex items-center gap-2 text-xs">
                <span>仅保留最近</span>
                <input type="number" min="1" v-model.number="rulePruneForm.keepLatestCount" class="input font-mono text-xs w-16 text-center py-0.5 inline-block" />
                <span>份快照，批量清理其余</span>
              </div>
            </label>
            <p class="text-[11px] text-amber-300">
              * 安全底线保护：处于加锁保护 🔒 状态的快照与最新 1 份快照将被自动跳过。
            </p>
          </div>

          <div class="flex items-center justify-end gap-2 pt-3 border-t border-cyber-line">
            <button type="button" class="btn" @click="showRulePruneModal = false">取消</button>
            <button type="button" class="btn btn-primary" @click="previewRulePrune()">开始预演</button>
          </div>
        </div>
      </div>

      <!-- 批量清理 / GFS 修剪 Dry-Run 预演确认弹窗 -->
      <div v-if="showPrunePreviewModal" class="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-cyber-line pb-3">
            <h3 class="font-display text-base text-neon-soft">{{ pruneActionTitle }}</h3>
            <button type="button" class="btn btn-xs" @click="showPrunePreviewModal = false">✕</button>
          </div>

          <div class="grid grid-cols-2 gap-3 p-3 rounded-xl bg-cyber-panel border border-cyber-line">
            <div>
              <div class="text-xs text-slate-400">待清理快照数</div>
              <div class="text-lg font-bold text-rose-400">{{ pruneResult.deletedCount }} 份</div>
            </div>
            <div>
              <div class="text-xs text-slate-400">锁定保护跳过数</div>
              <div class="text-lg font-bold text-amber-300">{{ pruneResult.skippedPinnedCount }} 份</div>
            </div>
            <div class="col-span-2 pt-2 border-t border-cyber-line">
              <div class="text-xs text-slate-400">预估释放物理空间</div>
              <div class="text-xl font-bold font-mono text-emerald-400">
                {{ formatBytes(pruneResult.estimatedReclaimedBytes) }}
              </div>
            </div>
          </div>

          <div v-if="pruneResult.deletedCount === 0" class="p-3 text-xs text-slate-500 text-center">
            未发现符合修剪条件的快照（或快照均已被锁定/熔断保护）。
          </div>

          <div v-else class="flex flex-col gap-1.5">
            <div class="text-xs font-medium text-slate-300">将受影响的快照清单：</div>
            <div class="max-h-36 overflow-y-auto p-2 rounded border border-cyber-line bg-cyber-bg text-xs font-mono text-slate-400 space-y-1">
              <div v-for="sid in pruneResult.affectedSnapshotIds" :key="sid">
                • {{ sid }}
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2 pt-3 border-t border-cyber-line">
            <button
              type="button"
              class="btn"
              @click="showPrunePreviewModal = false"
            >
              取消
            </button>
            <button
              type="button"
              class="btn btn-danger"
              :disabled="pruneResult.deletedCount === 0 || pruneExecuting"
              @click="executeConfirmedPrune()"
            >
              {{ pruneExecuting ? '正在清理中...' : '确认执行删除' }}
            </button>
          </div>
        </div>
      </div>

      <!-- 快照目录树浏览弹窗 -->
      <div v-if="showFileTreeModal" class="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
        <div class="panel w-full max-w-2xl p-5 max-h-[85vh] flex flex-col gap-3" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-cyber-line pb-3">
            <div>
              <h3 class="font-display text-base text-neon-soft">
                快照文件浏览: {{ currentSnapshot?.snapshotId }}
              </h3>
              <div class="text-xs text-slate-500 font-mono mt-0.5">{{ currentSnapshot?.snapshotPath }}</div>
            </div>
            <button type="button" class="btn btn-xs" @click="showFileTreeModal = false">✕</button>
          </div>

          <div v-if="fileTreeLoading" class="p-8 text-center text-xs text-slate-500">正在解析快照目录树...</div>
          <div v-else class="flex-1 overflow-y-auto space-y-1 font-mono text-xs">
            <div v-for="node in fileTreeNodes" :key="node.relativePath" class="p-1 hover:bg-cyber-panel-2 rounded flex items-center justify-between">
              <span class="flex items-center gap-1.5 text-slate-200">
                <span>{{ node.isDirectory ? '📁' : '📄' }}</span>
                <span>{{ node.relativePath }}</span>
              </span>
              <span v-if="!node.isDirectory" class="text-slate-500 text-[11px]">{{ formatBytes(node.sizeBytes) }}</span>
            </div>
          </div>

          <div class="flex justify-end pt-3 border-t border-cyber-line">
            <button type="button" class="btn" @click="showFileTreeModal = false">关闭</button>
          </div>
        </div>
      </div>

      <!-- 快照还原确认弹窗 -->
      <div v-if="showRestoreModal" class="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center justify-between border-b border-cyber-line pb-3">
            <h3 class="font-display text-base text-neon-soft">快照还原作业</h3>
            <button type="button" class="btn btn-xs" @click="showRestoreModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3">
            <div>
              <label class="block text-xs text-slate-400 mb-1">还原模式</label>
              <div class="flex flex-col gap-2">
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="restoreForm.restoreMode === 'source' ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
                  <input type="radio" v-model="restoreForm.restoreMode" value="source" class="accent-cyan-400" />
                  <div>
                    <div class="text-xs font-semibold text-slate-200">原位覆盖回滚 (In-place Restore)</div>
                    <div class="text-[11px] text-slate-400 mt-0.5">将快照数据直接写回原工作目录</div>
                  </div>
                </label>
                <label class="flex items-center gap-2 p-3 rounded-lg border cursor-pointer" :class="restoreForm.restoreMode === 'custom' ? 'border-cyan-500/50 bg-cyan-950/40 text-cyan-200' : 'border-cyber-line bg-cyber-panel/40 text-slate-300'">
                  <input type="radio" v-model="restoreForm.restoreMode" value="custom" class="accent-cyan-400" />
                  <div>
                    <div class="text-xs font-semibold text-slate-200">提取还原至指定新目录 (Custom Directory)</div>
                    <div class="text-[11px] text-slate-400 mt-0.5">安全还原到单独目录进行校验，不影响当前生产文件</div>
                  </div>
                </label>
              </div>
            </div>

            <div v-if="restoreForm.restoreMode === 'custom'">
              <label class="block text-xs text-slate-400 mb-1">目标提取目录</label>
              <input
                type="text"
                v-model="restoreForm.customPath"
                placeholder="/restore/20261011_extracted"
                class="input font-mono text-xs"
              />
            </div>

            <label class="flex items-center gap-2 text-xs text-slate-300 cursor-pointer pt-1">
              <input type="checkbox" v-model="restoreForm.cleanTarget" class="accent-cyan-400" />
              <span>还原前彻底清空目标目录已有文件</span>
            </label>
          </div>

          <div class="flex items-center justify-end gap-2 pt-3 border-t border-cyber-line">
            <button type="button" class="btn" @click="showRestoreModal = false">取消</button>
            <button
              type="button"
              class="btn btn-primary"
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
        @select="onPickerConfirm($event)"
        @close="picker.show = false"
      />
    </div>
  `,
});
