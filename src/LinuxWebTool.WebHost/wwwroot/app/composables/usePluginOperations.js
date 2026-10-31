import { ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

/**
 * 插件异步操作统一处理组合式函数：
 * - 统一负责操作提交、轮询、状态跟踪、取消与产物下载链接生成；
 * - 遵守前端 API 唯一层规范，不直接操作底层存储或私密 token。
 */
export function usePluginOperations() {
  const currentOperation = ref(null);
  const isPolling = ref(false);
  const progressPercent = ref(null);
  const progressMessage = ref('');
  const lastError = ref(null);

  async function submitOperation(invocation) {
    lastError.value = null;
    const res = await http(API.pluginOperations.submit, {
      method: 'POST',
      body: invocation,
    });

    if (res.ok && res.data) {
      return res.data; // OperationAccepted: { operationId, statusUrl, status }
    }

    lastError.value = res.message || '操作提交失败';
    toast.error(lastError.value);
    return null;
  }

  async function pollUntilComplete(operationId, onProgress) {
    if (!operationId) return null;
    isPolling.value = true;
    lastError.value = null;

    try {
      while (isPolling.value) {
        await new Promise((r) => setTimeout(r, 1000));
        if (!isPolling.value) break;

        const res = await http(API.pluginOperations.item(operationId));
        if (!res.ok || !res.data) {
          lastError.value = res.message || '查询操作状态失败';
          break;
        }

        const op = res.data;
        currentOperation.value = op;
        progressPercent.value = op.progress != null ? Math.round(op.progress) : null;
        progressMessage.value = op.progressMessage || (op.status === 'running' ? '正在执行中...' : '排队中...');

        if (typeof onProgress === 'function') {
          onProgress(op);
        }

        if (['succeeded', 'failed', 'cancelled', 'timedOut'].includes(op.status)) {
          isPolling.value = false;
          return op;
        }
      }
    } finally {
      isPolling.value = false;
    }

    return currentOperation.value;
  }

  async function cancelOperation(operationId) {
    const targetId = operationId || currentOperation.value?.operationId;
    if (!targetId) return false;

    try {
      const res = await http(API.pluginOperations.cancel(targetId), { method: 'POST' });
      if (res.ok) {
        toast.info('已请求取消操作');
        return true;
      }
      toast.error(res.message || '取消操作失败');
      return false;
    } catch (err) {
      toast.error('取消请求异常: ' + err.message);
      return false;
    }
  }

  function stopPolling() {
    isPolling.value = false;
  }

  function getArtifactDownloadUrl(operationId, artifactId) {
    return `/api${API.pluginOperations.artifactDownload(operationId, artifactId)}`;
  }

  return {
    currentOperation,
    isPolling,
    progressPercent,
    progressMessage,
    lastError,
    submitOperation,
    pollUntilComplete,
    cancelOperation,
    stopPolling,
    getArtifactDownloadUrl,
  };
}
