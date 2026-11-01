import { defineComponent, ref } from 'vue';
import { http, httpUpload } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginImportModal',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
  },
  emits: ['close', 'imported'],
  setup(props, { emit }) {
    const activeTab = ref('zip'); // 'zip' | 'repo'
    const zipFileInput = ref(null);
    const uploadingZip = ref(false);

    // Repo scan
    const repoUrl = ref('');
    const branch = ref('');
    const scanning = ref(false);
    const applying = ref(false);
    const scannedModules = ref([]);
    const selectedIds = ref([]);

    function handleClose() {
      emit('close');
    }

    async function handleZipUpload(event) {
      const file = event.target.files?.[0];
      if (!file) return;

      uploadingZip.value = true;
      try {
        const formData = new FormData();
        formData.append('file', file);
        const res = await httpUpload(API.plugins.importZip, formData);
        if (res.ok) {
          toast.success(res.data?.message || '插件 Zip 包导入成功');
          emit('imported');
          emit('close');
        } else {
          toast.error(res.message || '导入 Zip 失败');
        }
      } catch (err) {
        toast.error('上传插件异常: ' + err.message);
      } finally {
        uploadingZip.value = false;
        if (zipFileInput.value) zipFileInput.value.value = '';
      }
    }

    async function handleScanRepo() {
      if (!repoUrl.value.trim()) {
        toast.warning('请输入 Git 仓库地址');
        return;
      }
      scanning.value = true;
      scannedModules.value = [];
      selectedIds.value = [];
      try {
        const res = await http(API.plugins.repoScan, {
          method: 'POST',
          body: {
            repoUrl: repoUrl.value.trim(),
            branch: branch.value.trim() || null,
          },
        });
        if (res.ok && res.data) {
          scannedModules.value = res.data.modules || [];
          selectedIds.value = scannedModules.value.map((m) => m.id);
          toast.info(`扫描到 ${scannedModules.value.length} 个插件模块`);
        } else {
          toast.error(res.message || '扫描仓库失败');
        }
      } catch (err) {
        toast.error('扫描仓库异常: ' + err.message);
      } finally {
        scanning.value = false;
      }
    }

    function toggleSelect(id) {
      const idx = selectedIds.value.indexOf(id);
      if (idx >= 0) {
        selectedIds.value.splice(idx, 1);
      } else {
        selectedIds.value.push(id);
      }
    }

    function toggleSelectAll() {
      if (selectedIds.value.length === scannedModules.value.length) {
        selectedIds.value = [];
      } else {
        selectedIds.value = scannedModules.value.map((m) => m.id);
      }
    }

    async function handleApplyRepo() {
      if (selectedIds.value.length === 0) {
        toast.warning('请至少勾选一个插件模块');
        return;
      }
      applying.value = true;
      try {
        const res = await http(API.plugins.repoApply, {
          method: 'POST',
          body: {
            repoUrl: repoUrl.value.trim(),
            branch: branch.value.trim() || null,
            selectedModuleIds: selectedIds.value,
          },
        });
        if (res.ok && res.data) {
          toast.success(`成功导入 ${res.data.successCount || selectedIds.value.length} 个插件！`);
          emit('imported');
          emit('close');
        } else {
          toast.error(res.message || '批量导入插件失败');
        }
      } catch (err) {
        toast.error('导入插件异常: ' + err.message);
      } finally {
        applying.value = false;
      }
    }

    return {
      activeTab,
      zipFileInput,
      uploadingZip,
      repoUrl,
      branch,
      scanning,
      applying,
      scannedModules,
      selectedIds,
      handleClose,
      handleZipUpload,
      handleScanRepo,
      toggleSelect,
      toggleSelectAll,
      handleApplyRepo,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-2xl p-5 border border-cyan-500/40 flex flex-col max-h-[85vh] shadow-2xl">
        <!-- 弹窗头部 -->
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line/60">
          <div class="flex items-center gap-2">
            <span class="text-xl">📦</span>
            <div>
              <h3 class="font-display font-semibold text-slate-100 text-sm">
                导入与扫描安装插件
              </h3>
              <div class="text-xs text-slate-400">
                支持单个 Zip 文件导入，或直接从 Git 仓库多模块扫描批量安装
              </div>
            </div>
          </div>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-white" @click="handleClose()">
            ✕
          </button>
        </div>

        <!-- 标签页切换 -->
        <div class="flex border-b border-cyber-line/50 mt-3">
          <button
            type="button"
            class="px-4 py-2 text-xs font-medium border-b-2 transition-colors"
            :class="activeTab === 'zip' ? 'border-cyan-400 text-cyan-300' : 'border-transparent text-slate-400 hover:text-slate-200'"
            @click="activeTab = 'zip'"
          >
            📁 Zip 压缩包上传
          </button>
          <button
            type="button"
            class="px-4 py-2 text-xs font-medium border-b-2 transition-colors"
            :class="activeTab === 'repo' ? 'border-cyan-400 text-cyan-300' : 'border-transparent text-slate-400 hover:text-slate-200'"
            @click="activeTab = 'repo'"
          >
            🐙 Git 仓库扫描
          </button>
        </div>

        <!-- 标签页 1: Zip 上传 -->
        <div v-if="activeTab === 'zip'" class="py-6 flex flex-col items-center justify-center space-y-4">
          <div class="w-16 h-16 rounded-2xl bg-cyan-500/10 border border-cyan-500/30 flex items-center justify-center text-3xl">
            📦
          </div>
          <div class="text-center">
            <div class="text-sm font-semibold text-slate-200">选择包含 plugin.json 的 Zip 压缩包</div>
            <div class="text-xs text-slate-400 mt-1">系统将自动解压、安全校验并注册为可用插件</div>
          </div>
          <label class="btn-primary text-xs px-4 py-2 cursor-pointer flex items-center gap-2">
            <span v-if="uploadingZip" class="animate-spin">⏳</span>
            <span>{{ uploadingZip ? '正在上传解压...' : '选择 Zip 文件' }}</span>
            <input
              ref="zipFileInput"
              type="file"
              accept=".zip"
              class="hidden"
              :disabled="uploadingZip"
              @change="handleZipUpload($event)"
            />
          </label>
        </div>

        <!-- 标签页 2: Git 扫描 -->
        <div v-else class="py-4 space-y-4 overflow-y-auto flex-1 min-h-0">
          <div class="space-y-2">
            <label class="text-xs text-slate-300">Git 仓库地址 (HTTPS / Git):</label>
            <div class="flex gap-2">
              <input
                v-model="repoUrl"
                type="text"
                class="input-base text-xs flex-1"
                placeholder="例如: https://github.com/owner/my-plugins.git"
              />
              <input
                v-model="branch"
                type="text"
                class="input-base text-xs w-28"
                placeholder="分支 (可选)"
              />
              <button
                type="button"
                class="btn-secondary text-xs px-3 whitespace-nowrap"
                :disabled="scanning"
                @click="handleScanRepo()"
              >
                <span>{{ scanning ? '扫描中...' : '开始扫描' }}</span>
              </button>
            </div>
          </div>

          <!-- 扫描结果列表 -->
          <div v-if="scannedModules.length > 0" class="space-y-2">
            <div class="flex items-center justify-between text-xs text-slate-400">
              <span>发现 {{ scannedModules.length }} 个插件，已选 {{ selectedIds.length }} 个:</span>
              <button type="button" class="text-cyan-400 hover:underline" @click="toggleSelectAll()">
                {{ selectedIds.length === scannedModules.length ? '取消全选' : '全选' }}
              </button>
            </div>

            <div class="max-h-48 overflow-y-auto space-y-1.5 p-2 bg-slate-950 rounded border border-cyber-line/50">
              <div
                v-for="mod in scannedModules"
                :key="mod.id"
                class="flex items-center gap-2.5 p-2 rounded hover:bg-slate-900 transition-colors cursor-pointer text-xs"
                @click="toggleSelect(mod.id)"
              >
                <input
                  type="checkbox"
                  :checked="selectedIds.includes(mod.id)"
                  class="rounded bg-slate-900 border-slate-700 text-cyan-500"
                  @click.stop="toggleSelect(mod.id)"
                />
                <div class="flex-1 min-w-0">
                  <div class="font-medium text-slate-200 truncate">{{ mod.name }} ({{ mod.id }})</div>
                  <div class="text-[11px] text-slate-400 truncate">{{ mod.description || '无描述' }}</div>
                </div>
                <span class="text-[10px] px-1.5 py-0.5 rounded bg-slate-800 text-slate-300 shrink-0 font-mono">
                  {{ mod.profile === 0 ? '⚡常驻' : '🔌脚本' }}
                </span>
              </div>
            </div>

            <div class="pt-2 flex justify-end">
              <button
                type="button"
                class="btn-primary text-xs px-4 py-2 flex items-center gap-1.5"
                :disabled="applying || selectedIds.length === 0"
                @click="handleApplyRepo()"
              >
                <span v-if="applying" class="animate-spin">⏳</span>
                <span>{{ applying ? '正在导入安装...' : '确认批量安装所选项' }}</span>
              </button>
            </div>
          </div>
        </div>

        <!-- 底部 -->
        <div class="pt-3 border-t border-cyber-line/50 flex justify-end">
          <button type="button" class="btn-ghost text-xs" @click="handleClose()">
            关闭
          </button>
        </div>
      </div>
    </div>
  `,
});
