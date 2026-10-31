import { computed, defineComponent, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionRepoImportModal',
  props: {
    show: { type: Boolean, default: false },
  },
  emits: ['close', 'imported'],
  setup(props, { emit }) {
    const inspecting = ref(false);
    const importing = ref(false);
    const inspected = ref(false);

    const form = reactive({
      repoUrl: '',
      proxyPrefix: '',
      branch: 'main',
      overwrite: true,
    });

    const discoveredModules = ref([]);
    const selectedPaths = ref([]);

    function closeModal() {
      emit('close');
    }

    const allSelected = computed(() => {
      const selectable = discoveredModules.value.filter((m) => m.isCompatibleWithCurrentOs);
      return selectable.length > 0 && selectedPaths.value.length === selectable.length;
    });

    function toggleSelectAll() {
      if (allSelected.value) {
        selectedPaths.value = [];
      } else {
        selectedPaths.value = discoveredModules.value
          .filter((m) => m.isCompatibleWithCurrentOs)
          .map((m) => m.relativePath);
      }
    }

    function toggleModuleSelection(relPath, isCompatible) {
      if (!isCompatible) return;
      const idx = selectedPaths.value.indexOf(relPath);
      if (idx >= 0) {
        selectedPaths.value.splice(idx, 1);
      } else {
        selectedPaths.value.push(relPath);
      }
    }

    async function inspectRepo() {
      if (!form.repoUrl.trim()) {
        toast.warning('请输入 GitHub 仓库地址 (如 owner/repo 或完整链接)');
        return;
      }

      inspecting.value = true;
      discoveredModules.value = [];
      selectedPaths.value = [];
      inspected.value = false;

      try {
        const res = await http(API.services.extensionRepoInspect, {
          method: 'POST',
          body: {
            repositoryUrl: form.repoUrl.trim(),
            proxyPrefix: form.proxyPrefix || null,
            branch: form.branch.trim() || 'main',
            targetType: 'extension',
          },
        });

        if (res.ok && res.data) {
          discoveredModules.value = res.data.modules || [];
          inspected.value = true;
          if (discoveredModules.value.length === 0) {
            toast.warning('在指定的仓库中未扫描到有效的 manifest.json 扩展模块');
          } else {
            toast.success(`扫描完成，发现 ${discoveredModules.value.length} 个扩展模块`);
            // 默认勾选兼容的模块
            selectedPaths.value = discoveredModules.value
              .filter((m) => m.isCompatibleWithCurrentOs)
              .map((m) => m.relativePath);
          }
        } else {
          toast.error(res.message || '拉取或解析仓库失败');
        }
      } catch (err) {
        toast.error('探测异常: ' + err.message);
      } finally {
        inspecting.value = false;
      }
    }

    async function doBatchImport() {
      if (selectedPaths.value.length === 0) {
        toast.warning('请至少勾选一个要导入的扩展模块');
        return;
      }

      importing.value = true;
      try {
        const res = await http(API.services.extensionRepoImport, {
          method: 'POST',
          body: {
            repositoryUrl: form.repoUrl.trim(),
            proxyPrefix: form.proxyPrefix || null,
            branch: form.branch.trim() || 'main',
            targetType: 'extension',
            selectedModulePaths: selectedPaths.value,
            overwriteExisting: form.overwrite,
          },
        });

        if (res.ok && res.data?.success) {
          toast.success(res.data.message || '扩展批量导入完成！');
          emit('imported');
          closeModal();
        } else {
          toast.error(res.data?.message || res.message || '批量导入失败');
        }
      } catch (err) {
        toast.error('导入异常: ' + err.message);
      } finally {
        importing.value = false;
      }
    }

    return {
      inspecting,
      importing,
      inspected,
      form,
      discoveredModules,
      selectedPaths,
      allSelected,
      closeModal,
      toggleSelectAll,
      toggleModuleSelection,
      inspectRepo,
      doBatchImport,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm">
      <div class="panel max-w-3xl w-full p-5 flex flex-col gap-4 border-cyan-500/50 animate-fade-in max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">🌐</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">从 GitHub 导入扩展模块 (单仓多模块)</div>
              <div class="text-[11px] text-slate-400 mt-0.5">支持输入 GitHub 仓库，自动递归扫描 manifest 并提供批量导入</div>
            </div>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>

        <!-- 仓库配置区域 -->
        <div class="grid grid-cols-1 md:grid-cols-12 gap-3 bg-cyber-bg/60 p-3 rounded border border-cyber-line/40">
          <div class="md:col-span-6 space-y-1">
            <label class="text-xs text-slate-300">GitHub 仓库路径 / 链接:</label>
            <input
              v-model="form.repoUrl"
              type="text"
              class="input text-xs w-full font-mono"
              placeholder="例如 owner/repo 或 https://github.com/..."
              @keyup.enter="inspectRepo()"
            />
          </div>
          <div class="md:col-span-3 space-y-1">
            <label class="text-xs text-slate-300">分支 (Branch):</label>
            <input v-model="form.branch" type="text" class="input text-xs w-full font-mono" placeholder="main" />
          </div>
          <div class="md:col-span-3 space-y-1">
            <label class="text-xs text-slate-300">网络加速代理:</label>
            <select v-model="form.proxyPrefix" class="select text-xs w-full">
              <option value="">直连 (官方源)</option>
              <option value="https://ghproxy.net/">ghproxy.net (国内镜像)</option>
              <option value="https://mirror.ghproxy.com/">mirror.ghproxy.com</option>
            </select>
          </div>
          <div class="md:col-span-12 flex items-center justify-between pt-1">
            <label class="flex items-center gap-1.5 text-xs text-slate-400 cursor-pointer">
              <input v-model="form.overwrite" type="checkbox" class="checkbox" />
              <span>覆写已存在的同名扩展 (保留历史 config.json 配置)</span>
            </label>
            <button
              type="button"
              class="btn btn-xs btn-primary min-w-[120px]"
              :disabled="inspecting"
              @click="inspectRepo()"
            >
              {{ inspecting ? '正在拉取与扫描...' : '🔍 探测仓库模块' }}
            </button>
          </div>
        </div>

        <!-- 模块清单展示区域 (Mono-repo) -->
        <div v-if="inspected" class="space-y-2">
          <div class="flex items-center justify-between text-xs text-slate-300">
            <div class="flex items-center gap-2">
              <span class="font-semibold">发现的扩展模块清单:</span>
              <span class="text-slate-400">共 {{ discoveredModules.length }} 项</span>
            </div>
            <button
              v-if="discoveredModules.length > 0"
              type="button"
              class="text-xs text-cyan-400 hover:text-cyan-300"
              @click="toggleSelectAll()"
            >
              {{ allSelected ? '取消全选' : '全选所有兼容项' }}
            </button>
          </div>

          <div v-if="discoveredModules.length === 0" class="p-6 text-center text-xs text-slate-500 bg-cyber-bg/40 rounded border border-cyber-line/30">
            未在该仓库发现可用的扩展模块（未检索到包含 manifest.json 的子目录）
          </div>

          <div v-else class="space-y-2 max-h-60 overflow-y-auto pr-1">
            <div
              v-for="item in discoveredModules"
              :key="item.relativePath"
              class="p-2.5 rounded border transition-colors flex items-start gap-3"
              :class="[
                selectedPaths.includes(item.relativePath)
                  ? 'bg-cyan-950/20 border-cyan-500/50'
                  : 'bg-cyber-bg/40 border-cyber-line/40 hover:border-slate-600',
                !item.isCompatibleWithCurrentOs ? 'opacity-50' : 'cursor-pointer'
              ]"
              @click="toggleModuleSelection(item.relativePath, item.isCompatibleWithCurrentOs)"
            >
              <input
                type="checkbox"
                class="checkbox mt-0.5"
                :checked="selectedPaths.includes(item.relativePath)"
                :disabled="!item.isCompatibleWithCurrentOs"
                @click.stop
                @change="toggleModuleSelection(item.relativePath, item.isCompatibleWithCurrentOs)"
              />
              <div class="text-lg">{{ item.manifest.icon || '🔌' }}</div>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <span class="font-semibold text-xs text-slate-200">{{ item.manifest.name }}</span>
                  <span class="text-[10px] font-mono text-cyan-400">v{{ item.manifest.version }}</span>
                  <span class="text-[10px] px-1.5 py-0.2 rounded bg-slate-800 text-slate-400 font-mono">{{ item.manifest.id }}</span>
                  <span v-if="item.alreadyInstalled" class="badge text-[10px] border-emerald-500/40 text-emerald-400">本地已装</span>
                  <span v-if="!item.isCompatibleWithCurrentOs" class="badge text-[10px] border-rose-500/40 text-rose-400">平台不兼容</span>
                  <span v-if="item.hasMissingRunners" class="badge text-[10px] border-amber-500/40 text-amber-300">
                    缺失运行环境: {{ item.missingRunners.join(', ') }}
                  </span>
                </div>
                <div class="text-[11px] text-slate-400 mt-0.5 truncate">{{ item.manifest.description || '暂无描述' }}</div>
                <div class="text-[10px] text-slate-500 font-mono mt-0.5">目录: {{ item.relativePath }}</div>
              </div>
            </div>
          </div>
        </div>

        <!-- 底部导入控制 -->
        <div class="flex items-center justify-between border-t border-cyber-line/50 pt-3">
          <div class="text-xs text-slate-400">
            已勾选 <span class="font-bold text-cyan-400">{{ selectedPaths.length }}</span> 个模块
          </div>
          <div class="flex items-center gap-2">
            <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">
              取消
            </button>
            <button
              v-if="inspected && discoveredModules.length > 0"
              type="button"
              class="btn btn-xs btn-primary"
              :disabled="importing || selectedPaths.length === 0"
              @click="doBatchImport()"
            >
              {{ importing ? '正在导入落盘...' : ('📥 批量导入所选 (' + selectedPaths.length + ' 项)') }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
