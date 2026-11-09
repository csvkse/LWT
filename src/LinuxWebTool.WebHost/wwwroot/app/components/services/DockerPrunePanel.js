import { defineComponent, onMounted, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'DockerPrunePanel',
  props: {
    formatBytes: { type: Function, required: true },
  },
  emits: ['pruned', 'refreshed'],
  setup(props, { emit }) {
    const diskUsage = ref(null);
    const diskUsageLoading = ref(false);
    const pruning = ref(false);
    const pruneActionName = ref('');
    const pruneOutput = ref('');
    const showPruneModal = ref(false);

    async function loadDiskUsage() {
      diskUsageLoading.value = true;
      try {
        const res = await http(API.services.dockerDiskUsage);
        if (res.ok && res.data) {
          diskUsage.value = res.data;
          emit('refreshed', diskUsage.value);
        } else {
          toast.error(res.message || '加载 Docker 磁盘占用失败');
        }
      } catch (err) {
        toast.error('获取磁盘占用异常: ' + err.message);
      } finally {
        diskUsageLoading.value = false;
      }
    }

    async function executePrune(type, label, withVolumes = false) {
      if (!confirm(`确定要执行【${label}】吗？\n该操作将清除匹配的 Docker 缓存或未引用资源。`)) {
        return;
      }
      pruning.value = true;
      pruneActionName.value = label;
      pruneOutput.value = `正在执行 ${label}，请稍候...`;
      showPruneModal.value = true;
      try {
        const res = await http(API.services.dockerPrune, {
          method: 'POST',
          body: { pruneType: type, all: true, volumes: withVolumes },
        });
        if (res.ok && res.data?.success) {
          toast.success(res.data.message || `${label} 执行成功`);
          pruneOutput.value = res.data.output || res.data.message || '清理完成';
          await loadDiskUsage();
          emit('pruned', { type, result: res.data });
        } else {
          const errMsg = res.data?.message || res.message || `${label} 失败`;
          toast.error(errMsg);
          pruneOutput.value = `执行失败:\n${errMsg}\n\n${res.data?.output || ''}`;
        }
      } catch (err) {
        toast.error(`执行异常: ${err.message}`);
        pruneOutput.value = `异常中断: ${err.message}`;
      } finally {
        pruning.value = false;
      }
    }

    onMounted(() => {
      loadDiskUsage();
    });

    return {
      diskUsage,
      diskUsageLoading,
      pruning,
      pruneActionName,
      pruneOutput,
      showPruneModal,
      loadDiskUsage,
      executePrune,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div v-if="diskUsageLoading && !diskUsage" class="panel p-8 text-center text-slate-500 text-xs">
        正在检索 Docker 存储空间占用 (docker system df)...
      </div>
      <div v-else class="flex flex-col gap-4">
        <!-- 总体概览横幅 -->
        <div class="panel p-4 border-cyan-500/30 bg-cyber-panel/60 flex flex-col md:flex-row items-start md:items-center justify-between gap-3">
          <div class="flex flex-col gap-1">
            <div class="flex items-center gap-2">
              <span class="text-base">🧹</span>
              <span class="font-display font-semibold text-slate-100 text-sm">Docker 存储占用与空间回收</span>
              <span v-if="diskUsage" class="badge text-[11px] border-cyan-500/50 text-cyan-300 bg-cyan-500/10">
                可回收空间: {{ formatBytes(diskUsage.totalReclaimableBytes) }}
              </span>
            </div>
            <p class="text-xs text-slate-400">
              支持按构建缓存、闲置镜像、停止容器及数据卷分类按需释放，杜绝宿主机磁盘被 BuildKit 和旧镜像撑满。
            </p>
          </div>
          <div class="flex items-center gap-2 shrink-0">
            <button
              type="button"
              class="btn btn-xs hover:border-cyan-500/40"
              :disabled="diskUsageLoading || pruning"
              @click="loadDiskUsage()"
            >
              🔄 刷新统计
            </button>
            <button
              type="button"
              class="btn btn-xs border-amber-500/50 text-amber-300 hover:bg-amber-500/20"
              :disabled="pruning"
              @click="executePrune('system', '全系统安全清理 (System Prune)')"
              title="清除所有未被使用的容器、网络和悬空镜像"
            >
              ⚡ 一键系统清理
            </button>
          </div>
        </div>

        <!-- 四分类卡片网格 -->
        <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3">
          <!-- 1. 构建缓存 Build Cache -->
          <div class="panel p-3.5 flex flex-col justify-between gap-3 border-cyber-line/60">
            <div class="flex flex-col gap-1.5">
              <div class="flex items-center justify-between">
                <span class="text-xs font-semibold text-slate-200 flex items-center gap-1.5">
                  <span>🔨</span> BuildKit 构建缓存
                </span>
                <span class="badge text-[10px] border-amber-500/40 text-amber-300">
                  {{ diskUsage?.buildCache?.totalCount || 0 }} 缓存层
                </span>
              </div>
              <div class="flex items-baseline justify-between mt-1">
                <span class="text-xs text-slate-400">占用大小:</span>
                <span class="font-mono text-sm font-semibold text-cyan-300">
                  {{ formatBytes(diskUsage?.buildCache?.totalBytes) }}
                </span>
              </div>
              <div class="flex items-baseline justify-between">
                <span class="text-xs text-slate-400">可安全回收:</span>
                <span class="font-mono text-xs font-semibold text-emerald-400">
                  {{ formatBytes(diskUsage?.buildCache?.reclaimableBytes) }}
                </span>
              </div>
              <div class="text-[11px] text-slate-500 mt-0.5">
                镜像构建产生的中间层缓存，清除不会破坏任何运行中的容器。
              </div>
            </div>
            <button
              type="button"
              class="btn btn-xs border-amber-500/40 text-amber-300 hover:bg-amber-500/10 w-full"
              :disabled="pruning"
              @click="executePrune('builder', '构建缓存清理 (docker builder prune -a)')"
            >
              ⚡ 清理构建缓存
            </button>
          </div>

          <!-- 2. 本地镜像 Images -->
          <div class="panel p-3.5 flex flex-col justify-between gap-3 border-cyber-line/60">
            <div class="flex flex-col gap-1.5">
              <div class="flex items-center justify-between">
                <span class="text-xs font-semibold text-slate-200 flex items-center gap-1.5">
                  <span>📦</span> 本地 Docker 镜像
                </span>
                <span class="badge text-[10px] border-cyan-500/40 text-cyan-300">
                  {{ diskUsage?.images?.activeCount || 0 }} 活跃 / {{ diskUsage?.images?.totalCount || 0 }} 总数
                </span>
              </div>
              <div class="flex items-baseline justify-between mt-1">
                <span class="text-xs text-slate-400">占用大小:</span>
                <span class="font-mono text-sm font-semibold text-cyan-300">
                  {{ formatBytes(diskUsage?.images?.totalBytes) }}
                </span>
              </div>
              <div class="flex items-baseline justify-between">
                <span class="text-xs text-slate-400">可安全回收:</span>
                <span class="font-mono text-xs font-semibold text-emerald-400">
                  {{ formatBytes(diskUsage?.images?.reclaimableBytes) }}
                </span>
              </div>
              <div class="text-[11px] text-slate-500 mt-0.5">
                未被任何运行或停止容器引用的历史无用镜像与悬空层。
              </div>
            </div>
            <button
              type="button"
              class="btn btn-xs hover:border-cyan-500/40 text-cyan-300 w-full"
              :disabled="pruning"
              @click="executePrune('images', '闲置镜像清理 (docker image prune -a)')"
            >
              ⚡ 清理闲置镜像
            </button>
          </div>

          <!-- 3. 容器实例 Containers -->
          <div class="panel p-3.5 flex flex-col justify-between gap-3 border-cyber-line/60">
            <div class="flex flex-col gap-1.5">
              <div class="flex items-center justify-between">
                <span class="text-xs font-semibold text-slate-200 flex items-center gap-1.5">
                  <span>🐳</span> 容器实例写层
                </span>
                <span class="badge text-[10px]" :class="(diskUsage?.containers?.totalCount - diskUsage?.containers?.activeCount) > 0 ? 'border-amber-500/40 text-amber-300' : 'border-slate-700 text-slate-500'">
                  {{ diskUsage?.containers?.activeCount || 0 }} 运行 / {{ diskUsage?.containers?.totalCount || 0 }} 总数
                </span>
              </div>
              <div class="flex items-baseline justify-between mt-1">
                <span class="text-xs text-slate-400">可写层大小:</span>
                <span class="font-mono text-sm font-semibold text-cyan-300">
                  {{ formatBytes(diskUsage?.containers?.totalBytes) }}
                </span>
              </div>
              <div class="flex items-baseline justify-between">
                <span class="text-xs text-slate-400">停止容器可回收:</span>
                <span class="font-mono text-xs font-semibold text-emerald-400">
                  {{ formatBytes(diskUsage?.containers?.reclaimableBytes) }}
                </span>
              </div>
              <div class="text-[11px] text-slate-500 mt-0.5">
                已退出（Exited）的历史临时容器写层，清理后不可恢复。
              </div>
            </div>
            <button
              type="button"
              class="btn btn-xs hover:border-slate-500/40 text-slate-300 w-full"
              :disabled="pruning"
              @click="executePrune('containers', '停止容器清理 (docker container prune)')"
            >
              ⚡ 清理已停止容器
            </button>
          </div>

          <!-- 4. 本地数据卷 Volumes -->
          <div class="panel p-3.5 flex flex-col justify-between gap-3 border-cyber-line/60">
            <div class="flex flex-col gap-1.5">
              <div class="flex items-center justify-between">
                <span class="text-xs font-semibold text-slate-200 flex items-center gap-1.5">
                  <span>💽</span> 本地数据卷 (Volumes)
                </span>
                <span class="badge text-[10px] border-slate-700 text-slate-400">
                  {{ diskUsage?.localVolumes?.activeCount || 0 }} 在用 / {{ diskUsage?.localVolumes?.totalCount || 0 }} 卷
                </span>
              </div>
              <div class="flex items-baseline justify-between mt-1">
                <span class="text-xs text-slate-400">数据卷大小:</span>
                <span class="font-mono text-sm font-semibold text-cyan-300">
                  {{ formatBytes(diskUsage?.localVolumes?.totalBytes) }}
                </span>
              </div>
              <div class="flex items-baseline justify-between">
                <span class="text-xs text-slate-400">无主卷可回收:</span>
                <span class="font-mono text-xs font-semibold text-emerald-400">
                  {{ formatBytes(diskUsage?.localVolumes?.reclaimableBytes) }}
                </span>
              </div>
              <div class="text-[11px] text-slate-500 mt-0.5">
                未关联任何容器的孤立数据卷，清理前请确认无持久化数据。
              </div>
            </div>
            <button
              type="button"
              class="btn btn-xs hover:border-rose-500/40 text-rose-300 w-full"
              :disabled="pruning"
              @click="executePrune('volumes', '无主数据卷清理 (docker volume prune)')"
            >
              ⚡ 清理无主数据卷
            </button>
          </div>
        </div>
      </div>

      <!-- 清理执行日志弹窗 -->
      <div v-if="showPruneModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-2xl w-full max-h-[80vh] flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm flex items-center gap-2">
              <span v-if="pruning" class="inline-block animate-spin">🔄</span>
              <span>{{ pruneActionName }} 执行日志</span>
            </span>
            <button type="button" class="btn btn-xs" :disabled="pruning" @click="showPruneModal = false">✕ 关闭</button>
          </div>
          <div class="p-4 flex-1 overflow-auto bg-black/50">
            <pre class="font-mono text-xs text-slate-300 whitespace-pre-wrap leading-relaxed select-text">{{ pruneOutput }}</pre>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2">
            <button type="button" class="btn btn-xs btn-primary" :disabled="pruning" @click="showPruneModal = false">完成</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
