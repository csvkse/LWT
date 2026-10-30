import { defineComponent, onMounted, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'SystemdSubmodule',
  props: {
    env: { type: Object, default: () => ({}) },
  },
  setup(props) {
    const loading = ref(false);
    const units = ref([]);
    const keyword = ref('');
    const stateFilter = ref('all');
    const typeFilter = ref('service');

    // 日志与 Unit 文件弹窗
    const showModal = ref(false);
    const modalTitle = ref('');
    const modalContent = ref('');
    const modalLoading = ref(false);

    async function loadUnits() {
      loading.value = true;
      try {
        const url = API.services.systemdUnits(keyword.value, stateFilter.value, typeFilter.value);
        const res = await http(url);
        if (res.ok && res.data) {
          units.value = res.data;
        } else {
          toast.error(res.message || '加载 Systemd 单元列表失败');
        }
      } catch (err) {
        toast.error('网络请求失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function executeAction(unitName, actionCode, actionDesc) {
      try {
        const res = await http(API.services.systemdAction, {
          method: 'POST',
          body: { serviceId: unitName, action: actionCode },
        });
        if (res.ok && res.data?.success) {
          toast.success(`服务 [${unitName}] ${actionDesc} 成功`);
          await loadUnits();
        } else {
          toast.error(res.data?.message || res.message || `${actionDesc} 失败`);
        }
      } catch (err) {
        toast.error(`操作异常: ${err.message}`);
      }
    }

    async function viewJournal(unitName) {
      modalTitle.value = `运行日志 (journalctl -u ${unitName})`;
      modalContent.value = '正在拉取实时日志...';
      modalLoading.value = true;
      showModal.value = true;
      try {
        const res = await http(API.services.systemdJournal(unitName, 150));
        if (res.ok && res.data?.lines) {
          modalContent.value = res.data.lines.join('\n') || '暂无日志输出';
        } else {
          modalContent.value = '拉取日志失败: ' + (res.message || '未知错误');
        }
      } catch (err) {
        modalContent.value = '拉取日志异常: ' + err.message;
      } finally {
        modalLoading.value = false;
      }
    }

    async function viewUnitFile(unitName) {
      modalTitle.value = `配置文件 (${unitName})`;
      modalContent.value = '正在读取单元定义...';
      modalLoading.value = true;
      showModal.value = true;
      try {
        const res = await http(API.services.systemdUnitFile(unitName));
        if (res.ok && res.data?.content) {
          modalContent.value = res.data.content;
        } else {
          modalContent.value = '读取失败: ' + (res.message || '无法获取配置');
        }
      } catch (err) {
        modalContent.value = '读取异常: ' + err.message;
      } finally {
        modalLoading.value = false;
      }
    }

    function closeModal() {
      showModal.value = false;
    }

    onMounted(() => {
      loadUnits();
    });

    return {
      loading,
      units,
      keyword,
      stateFilter,
      typeFilter,
      showModal,
      modalTitle,
      modalContent,
      modalLoading,
      loadUnits,
      executeAction,
      viewJournal,
      viewUnitFile,
      closeModal,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 顶部控制与过滤栏 -->
      <div class="panel p-3 flex flex-wrap items-center justify-between gap-3">
        <div class="flex flex-wrap items-center gap-2 flex-1 min-w-[18rem]">
          <input
            type="text"
            v-model="keyword"
            placeholder="搜索服务名称或描述 (如 nginx, ssh, docker)..."
            class="input text-xs flex-1 min-w-[12rem]"
            @keyup.enter="loadUnits()"
          />
          <select v-model="stateFilter" class="input text-xs w-28" @change="loadUnits()">
            <option value="all">全部状态</option>
            <option value="active">运行中 (Active)</option>
            <option value="inactive">已停止 (Inactive)</option>
            <option value="failed">异常失败 (Failed)</option>
          </select>
          <button type="button" class="btn btn-xs btn-primary shrink-0" :disabled="loading" @click="loadUnits()">
            🔍 筛选
          </button>
        </div>
        <div class="flex items-center gap-2">
          <span class="text-xs text-slate-400 font-mono">共 {{ units.length }} 个单元</span>
          <button type="button" class="btn btn-xs" :disabled="loading" @click="loadUnits()">
            🔄 刷新列表
          </button>
        </div>
      </div>

      <!-- 服务列表 -->
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
        正在拉取 Systemd 服务单元列表...
      </div>
      <div v-else-if="units.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        未匹配到符合条件的 Systemd 单元。
      </div>
      <div v-else class="panel overflow-x-auto">
        <table class="data-table min-w-[50rem]">
          <thead>
            <tr>
              <th>单元名称</th>
              <th>描述</th>
              <th>运行状态</th>
              <th>子状态</th>
              <th class="text-right">生命周期操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="u in units" :key="u.unit">
              <td class="font-mono text-xs font-medium text-slate-200">
                <div class="flex items-center gap-1.5">
                  <span
                    class="w-2 h-2 rounded-full shrink-0"
                    :class="u.activeState === 'active' ? 'bg-emerald-400' : (u.activeState === 'failed' ? 'bg-rose-500' : 'bg-slate-600')"
                  ></span>
                  <span class="truncate max-w-[16rem]" :title="u.unit">{{ u.unit }}</span>
                </div>
              </td>
              <td class="text-xs text-slate-400 truncate max-w-[20rem]" :title="u.description">
                {{ u.description || '-' }}
              </td>
              <td>
                <span
                  class="badge text-[11px]"
                  :class="u.activeState === 'active' ? 'border-emerald-500/50 text-emerald-300' : (u.activeState === 'failed' ? 'border-rose-500/50 text-rose-300' : 'border-slate-700 text-slate-500')"
                >
                  {{ u.activeState }}
                </span>
              </td>
              <td class="text-xs font-mono text-slate-400">
                {{ u.subState || '-' }}
              </td>
              <td class="text-right whitespace-nowrap">
                <div class="inline-flex items-center gap-1">
                  <button
                    v-if="u.activeState !== 'active'"
                    type="button"
                    class="btn btn-xs border-emerald-500/40 text-emerald-300 hover:bg-emerald-500/20"
                    @click="executeAction(u.unit, 1, '启动')"
                  >
                    启动
                  </button>
                  <button
                    v-else
                    type="button"
                    class="btn btn-xs border-rose-500/40 text-rose-300 hover:bg-rose-500/20"
                    @click="executeAction(u.unit, 2, '停止')"
                  >
                    停止
                  </button>
                  <button
                    type="button"
                    class="btn btn-xs hover:border-cyan-500/40"
                    @click="executeAction(u.unit, 3, '重启')"
                  >
                    重启
                  </button>
                  <button
                    type="button"
                    class="btn btn-xs"
                    @click="viewJournal(u.unit)"
                    title="查看服务日志"
                  >
                    日志
                  </button>
                  <button
                    type="button"
                    class="btn btn-xs"
                    @click="viewUnitFile(u.unit)"
                    title="查看配置文件"
                  >
                    配置
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 日志与配置模态弹窗 -->
      <div v-if="showModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 p-4 backdrop-blur-sm">
        <div class="panel max-w-4xl w-full max-h-[85vh] flex flex-col border-cyan-500/40 shadow-2xl">
          <div class="p-3.5 border-b border-cyber-line/50 flex items-center justify-between">
            <span class="font-display font-semibold text-slate-100 text-sm">{{ modalTitle }}</span>
            <button type="button" class="btn btn-xs" @click="closeModal()">✕ 关闭</button>
          </div>
          <div class="p-4 flex-1 overflow-auto bg-black/40">
            <pre class="font-mono text-xs text-slate-300 whitespace-pre-wrap leading-relaxed select-text">{{ modalContent }}</pre>
          </div>
          <div class="p-3 border-t border-cyber-line/50 flex justify-end">
            <button type="button" class="btn btn-xs btn-primary" @click="closeModal()">我知道了</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
