import { defineComponent, onMounted, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'DockerSubmodule',
  props: {
    env: { type: Object, default: () => ({}) },
  },
  setup() {
    const activeTab = ref('containers'); // containers | images
    const loading = ref(false);
    const containers = ref([]);
    const images = ref([]);

    // 日志弹窗
    const showLogsModal = ref(false);
    const logsContainerId = ref('');
    const logsContent = ref('');
    const logsLoading = ref(false);

    async function loadContainers() {
      loading.value = true;
      try {
        const res = await http(API.services.dockerContainers(true));
        if (res.ok && res.data) {
          containers.value = res.data;
        } else {
          toast.error(res.message || '加载容器列表失败');
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function loadImages() {
      loading.value = true;
      try {
        const res = await http(API.services.dockerImages);
        if (res.ok && res.data) {
          images.value = res.data;
        } else {
          toast.error(res.message || '加载镜像列表失败');
        }
      } catch (err) {
        toast.error('请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function executeContainerAction(containerId, actionCode, actionDesc) {
      try {
        const res = await http(API.services.dockerAction, {
          method: 'POST',
          body: {
            containerId,
            action: actionCode,
            removeVolumes: false,
            force: actionCode === 7,
          },
        });
        if (res.ok && res.data?.success) {
          toast.success(`容器 ${actionDesc} 成功`);
          await loadContainers();
        } else {
          toast.error(res.data?.message || res.message || `${actionDesc} 失败`);
        }
      } catch (err) {
        toast.error(`操作异常: ${err.message}`);
      }
    }

    async function viewLogs(containerId) {
      logsContainerId.value = containerId;
      logsContent.value = '正在拉取容器日志...';
      logsLoading.value = true;
      showLogsModal.value = true;
      try {
        const res = await http(API.services.dockerLogs(containerId, 200));
        if (res.ok && res.data?.logs) {
          logsContent.value = res.data.logs || '暂无日志输出';
        } else {
          logsContent.value = '拉取日志失败: ' + (res.message || '未知异常');
        }
      } catch (err) {
        logsContent.value = '拉取日志异常: ' + err.message;
      } finally {
        logsLoading.value = false;
      }
    }

    function closeLogs() {
      showLogsModal.value = false;
    }

    function formatBytes(bytes) {
      if (!bytes || bytes === 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return (bytes / Math.pow(k, i)).toFixed(1) + ' ' + sizes[i];
    }

    function formatPorts(ports) {
      if (!ports || ports.length === 0) return '-';
      return ports
        .map(p => (p.publicPort ? `${p.publicPort}->${p.privatePort}/${p.type || 'tcp'}` : `${p.privatePort}/${p.type || 'tcp'}`))
        .join(', ');
    }

    onMounted(() => {
      loadContainers();
    });

    return {
      activeTab,
      loading,
      containers,
      images,
      showLogsModal,
      logsContainerId,
      logsContent,
      logsLoading,
      loadContainers,
      loadImages,
      executeContainerAction,
      viewLogs,
      closeLogs,
      formatBytes,
      formatPorts,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 二级 Tab: 容器管理 vs 本地镜像 -->
      <div class="flex items-center justify-between border-b border-cyber-line/50 pb-2">
        <div class="flex items-center gap-2">
          <button
            type="button"
            class="nav-link py-1 px-3 text-xs"
            :class="activeTab === 'containers' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
            @click="activeTab = 'containers'; loadContainers()"
          >
            🐳 容器列表 ({{ containers.length }})
          </button>
          <button
            type="button"
            class="nav-link py-1 px-3 text-xs"
            :class="activeTab === 'images' ? '!border-cyan-400 text-cyan-300 bg-cyber-panel font-medium' : '!border-transparent text-slate-400 hover:text-slate-200'"
            @click="activeTab = 'images'; loadImages()"
          >
            📦 本地镜像 ({{ images.length }})
          </button>
        </div>
        <button
          type="button"
          class="btn btn-xs hover:border-cyan-500/40"
          :disabled="loading"
          @click="activeTab === 'containers' ? loadContainers() : loadImages()"
        >
          🔄 刷新数据
        </button>
      </div>

      <!-- 容器列表视图 -->
      <div v-if="activeTab === 'containers'">
        <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
          正在加载 Docker 容器状态...
        </div>
        <div v-else-if="containers.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          暂未发现任何 Docker 容器。可检查 Docker Daemon 或启动新容器。
        </div>
        <div v-else class="panel overflow-x-auto">
          <table class="data-table min-w-[52rem]">
            <thead>
              <tr>
                <th>容器名称</th>
                <th>镜像</th>
                <th>状态</th>
                <th>端口映射</th>
                <th class="text-right">容器操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="c in containers" :key="c.id">
                <td class="font-mono text-xs font-semibold text-slate-200">
                  <div class="flex items-center gap-2">
                    <span
                      class="w-2 h-2 rounded-full shrink-0"
                      :class="c.state === 'running' ? 'bg-emerald-400 animate-pulse' : 'bg-slate-600'"
                    ></span>
                    <span class="truncate max-w-[14rem]" :title="c.names?.join(', ') || c.id">
                      {{ c.names?.[0] || c.id.slice(0, 12) }}
                    </span>
                  </div>
                </td>
                <td class="text-xs text-slate-400 truncate max-w-[14rem]" :title="c.image">
                  {{ c.image }}
                </td>
                <td>
                  <span
                    class="badge text-[11px]"
                    :class="c.state === 'running' ? 'border-emerald-500/50 text-emerald-300 bg-emerald-500/10' : 'border-slate-700 text-slate-500'"
                  >
                    {{ c.status || c.state }}
                  </span>
                </td>
                <td class="text-xs font-mono text-cyan-300/80 truncate max-w-[14rem]" :title="formatPorts(c.ports)">
                  {{ formatPorts(c.ports) }}
                </td>
                <td class="text-right whitespace-nowrap">
                  <div class="inline-flex items-center gap-1">
                    <button
                      v-if="c.state !== 'running'"
                      type="button"
                      class="btn btn-xs border-emerald-500/40 text-emerald-300 hover:bg-emerald-500/20"
                      @click="executeContainerAction(c.id, 1, '启动')"
                    >
                      启动
                    </button>
                    <button
                      v-else
                      type="button"
                      class="btn btn-xs border-rose-500/40 text-rose-300 hover:bg-rose-500/20"
                      @click="executeContainerAction(c.id, 2, '停止')"
                    >
                      停止
                    </button>
                    <button
                      type="button"
                      class="btn btn-xs hover:border-cyan-500/40"
                      @click="executeContainerAction(c.id, 3, '重启')"
                    >
                      重启
                    </button>
                    <button
                      type="button"
                      class="btn btn-xs"
                      @click="viewLogs(c.id)"
                      title="查看容器日志"
                    >
                      日志
                    </button>
                    <button
                      type="button"
                      class="btn btn-xs btn-danger"
                      @click="executeContainerAction(c.id, 7, '删除')"
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

      <!-- 本地镜像视图 -->
      <div v-else>
        <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
          正在加载 Docker 镜像...
        </div>
        <div v-else-if="images.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
          未检索到本地 Docker 镜像。
        </div>
        <div v-else class="panel overflow-x-auto">
          <table class="data-table min-w-[40rem]">
            <thead>
              <tr>
                <th>镜像标签 (RepoTags)</th>
                <th>镜像 ID</th>
                <th>大小</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="img in images" :key="img.id">
                <td class="font-mono text-xs font-medium text-cyan-300">
                  {{ img.repoTags?.join(', ') || '<none>' }}
                </td>
                <td class="font-mono text-xs text-slate-400">
                  {{ img.id.replace('sha256:', '').slice(0, 12) }}
                </td>
                <td class="text-xs text-slate-400">
                  {{ formatBytes(img.size) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 容器日志弹窗 -->
      <div v-if="showLogsModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-4xl w-full max-h-[85vh] flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">容器实时日志 ({{ logsContainerId.slice(0, 12) }})</span>
            <button type="button" class="btn btn-xs" @click="closeLogs()">✕ 关闭</button>
          </div>
          <div class="p-4 flex-1 overflow-auto bg-black/50">
            <pre class="font-mono text-xs text-slate-300 whitespace-pre-wrap leading-relaxed select-text">{{ logsContent }}</pre>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end gap-2">
            <button type="button" class="btn btn-xs" :disabled="logsLoading" @click="viewLogs(logsContainerId)">🔄 重新拉取</button>
            <button type="button" class="btn btn-xs btn-primary" @click="closeLogs()">完成</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
