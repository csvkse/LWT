import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'WorkerUpdateModal',
  props: {
    show: { type: Boolean, default: false },
    worker: { type: Object, default: null },
  },
  emits: ['close', 'updated'],
  setup(props, { emit }) {
    const checking = ref(false);
    const updating = ref(false);
    const loadingSource = ref(false);
    const updateResult = ref(null);
    const sourceMetadata = ref(null);

    watch(
      () => props.show,
      async (val) => {
        if (val && props.worker) {
          updateResult.value = null;
          sourceMetadata.value = null;
          await loadSourceMetadata();
        }
      }
    );

    async function loadSourceMetadata() {
      if (!props.worker) return;
      loadingSource.value = true;
      try {
        const res = await http(API.workers.source(props.worker.id), { method: 'GET' });
        if (res.ok && res.data) {
          sourceMetadata.value = res.data;
        }
      } catch (err) {
        console.error('加载溯源元数据失败', err);
      } finally {
        loadingSource.value = false;
      }
    }

    async function checkUpdate() {
      if (!props.worker) return;
      checking.value = true;
      try {
        const res = await http(API.workers.checkUpdate(props.worker.id), { method: 'POST' });
        if (res.ok && res.data) {
          updateResult.value = res.data;
          if (res.data.hasUpdate) {
            toast.info(`检测到新版本: v${res.data.remoteVersion || res.data.remoteCommitHash?.slice(0, 7)}`);
          } else {
            toast.success(`当前已是最新版本 (v${res.data.currentVersion})`);
          }
        } else {
          toast.error(res.message || '检查更新失败');
        }
      } catch (err) {
        toast.error('检查更新异常: ' + err.message);
      } finally {
        checking.value = false;
      }
    }

    async function executeHotUpdate() {
      if (!props.worker) return;
      if (!window.confirm(`确定对函数微服务 [${props.worker.name}] 执行在线安全热更新吗？系统将自动快照保护 data.db 与 kv.db，且启动瞬退时会自动安全回滚。`)) return;

      updating.value = true;
      try {
        const res = await http(API.workers.hotUpdate(props.worker.id), {
          method: 'POST',
          body: { force: false },
        });
        if (res.ok && res.data?.success) {
          toast.success(res.data.message || '微服务热更新已成功生效！');
          emit('updated');
          emit('close');
        } else {
          toast.error(res.data?.message || res.message || '热更新失败');
          if (res.data?.rollbackPerformed) {
            toast.warning('新版本启动异常，系统已自动安全回滚至上一稳定版本！');
          }
          emit('updated');
        }
      } catch (err) {
        toast.error('热更新异常: ' + err.message);
      } finally {
        updating.value = false;
      }
    }

    async function toggleEnabled() {
      if (!props.worker) return;
      const next = !props.worker.isEnabled;
      try {
        const res = await http(API.workers.setEnabled(props.worker.id) + `?enabled=${next}`, {
          method: 'POST',
        });
        if (res.ok) {
          toast.success(`函数服务已成功${next ? '启用' : '禁用'}`);
          emit('updated');
        } else {
          toast.error(res.message || '更新状态失败');
        }
      } catch (err) {
        toast.error('操作异常: ' + err.message);
      }
    }

    return {
      checking,
      updating,
      loadingSource,
      updateResult,
      sourceMetadata,
      checkUpdate,
      executeHotUpdate,
      toggleEnabled,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm">
      <div class="panel max-w-lg w-full p-5 flex flex-col gap-3.5 border-cyan-500/40 animate-fade-in shadow-2xl">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-2.5">
          <div class="flex items-center gap-2">
            <span class="text-lg">⚡</span>
            <span class="font-display font-semibold text-slate-100 text-sm">
              函数服务治理与版本更新: <span class="font-mono text-cyan-400">{{ worker?.name }}</span>
            </span>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">✕</button>
        </div>

        <!-- 来源与元数据卡片 -->
        <div class="bg-black/30 p-3 rounded border border-cyber-line/30 space-y-1.5 text-xs font-mono">
          <div class="flex items-center justify-between">
            <span class="text-slate-400">服务唯一标识:</span>
            <span class="text-slate-200">#{{ worker?.id }}</span>
          </div>
          <div class="flex items-center justify-between">
            <span class="text-slate-400">当前安装版本:</span>
            <span class="text-cyan-300 font-semibold">v{{ sourceMetadata?.installedVersion || worker?.version || '1.0.0' }}</span>
          </div>
          <div class="flex items-center justify-between">
            <span class="text-slate-400">来源导入方式:</span>
            <span class="badge border-slate-700 text-slate-300 text-[10px]">
              {{ sourceMetadata?.sourceType === 'repo' ? '🐙 Git 仓库克隆' : (sourceMetadata?.sourceType === 'zip' ? '📦 Zip 压缩包导入' : '💻 本地创建') }}
            </span>
          </div>
          <div v-if="sourceMetadata?.repoUrl" class="flex flex-col gap-0.5 pt-1 border-t border-cyber-line/20">
            <span class="text-slate-400">上游仓库地址:</span>
            <span class="text-slate-300 truncate select-all text-[11px]">{{ sourceMetadata.repoUrl }}</span>
          </div>
          <div v-if="sourceMetadata?.installedCommitHash" class="flex items-center justify-between">
            <span class="text-slate-400">锁定 Commit:</span>
            <span class="text-slate-400 text-[11px]">{{ sourceMetadata.installedCommitHash.slice(0, 10) }}</span>
          </div>
          <div class="flex items-center justify-between pt-1 border-t border-cyber-line/20">
            <span class="text-slate-400">服务治理状态:</span>
            <button
              type="button"
              class="btn btn-xs py-0.5 px-2"
              :class="worker?.isEnabled !== false ? 'border-emerald-500/40 text-emerald-300' : 'border-rose-500/40 text-rose-300'"
              @click="toggleEnabled()"
            >
              {{ worker?.isEnabled !== false ? '🟢 已启用 (点击禁用)' : '🔴 已禁用 (点击启用)' }}
            </button>
          </div>
        </div>

        <!-- 检查更新展示面板 -->
        <div v-if="updateResult" class="p-3 rounded border text-xs space-y-2" :class="updateResult.hasUpdate ? 'bg-cyan-950/20 border-cyan-500/40' : 'bg-slate-900/40 border-cyber-line/30'">
          <div class="flex items-center justify-between">
            <span class="font-semibold text-slate-200">更新比对检测结果</span>
            <span class="badge" :class="updateResult.hasUpdate ? 'border-cyan-500 text-cyan-300' : 'border-slate-700 text-slate-400'">
              {{ updateResult.hasUpdate ? '发现可用新版本' : '已是最新版本' }}
            </span>
          </div>
          <div class="grid grid-cols-2 gap-2 font-mono text-[11px]">
            <div>当前本地版本: <span class="text-slate-300">v{{ updateResult.currentVersion }}</span></div>
            <div>远程最新版本: <span class="text-cyan-300 font-bold">v{{ updateResult.remoteVersion }}</span></div>
          </div>
          <div v-if="updateResult.releaseNotes" class="text-[11px] text-slate-400 bg-black/30 p-2 rounded whitespace-pre-wrap">
            {{ updateResult.releaseNotes }}
          </div>
        </div>

        <!-- 看门狗与保护提示 -->
        <div class="text-[11px] text-slate-400 leading-relaxed bg-black/20 p-2.5 rounded border border-cyber-line/20">
          💡 <span class="text-slate-300">安全热更新保障机制：</span>系统将在更新前生成完整快照，自动排除并保护独立的 <code class="text-cyan-300">data.db</code> 与 <code class="text-cyan-300">kv.db</code>。拉起新版本后将有 10 秒看门狗监控，若出现瞬崩闪退将自动触发无损回滚。
        </div>

        <!-- 弹窗底部操作按钮 -->
        <div class="flex items-center justify-between pt-2 border-t border-cyber-line/40">
          <button
            type="button"
            class="btn btn-xs hover:border-cyan-500/40 text-cyan-300"
            :disabled="checking || updating || !sourceMetadata?.repoUrl"
            @click="checkUpdate()"
          >
            {{ checking ? '正在比对版本...' : '🔍 检查上游更新' }}
          </button>
          <div class="flex items-center gap-2">
            <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">关闭</button>
            <button
              v-if="updateResult?.hasUpdate"
              type="button"
              class="btn btn-xs btn-primary font-semibold"
              :disabled="updating"
              @click="executeHotUpdate()"
            >
              {{ updating ? '正在执行原子更新...' : '⚡ 一键在线热更新' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
