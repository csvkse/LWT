import { defineComponent, ref, watch } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionUpdateModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: null },
  },
  emits: ['close', 'updated'],
  setup(props, { emit }) {
    const checking = ref(false);
    const updating = ref(false);
    const updateResult = ref(null);

    watch(() => props.show, (val) => {
      if (val && props.ext) {
        updateResult.value = props.ext.updateInfo || null;
      }
    });

    async function checkUpdate() {
      if (!props.ext) return;
      checking.value = true;
      try {
        const res = await http(API.services.extensionCheckUpdate(props.ext.manifest.id), { method: 'POST' });
        if (res.ok && res.data) {
          updateResult.value = res.data;
          props.ext.updateInfo = res.data;
          if (res.data.hasUpdate) {
            toast.info(`检测到新版本: v${res.data.remoteVersion}`);
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
      if (!props.ext) return;
      if (!window.confirm(`确定对 [${props.ext.manifest.name}] 执行在线安全热更新吗？系统将自动创建快照并在启动异常时安全回滚。`)) return;

      updating.value = true;
      try {
        const res = await http(API.services.extensionHotUpdate(props.ext.manifest.id), {
          method: 'POST',
          body: { force: false },
        });
        if (res.ok && res.data?.success) {
          toast.success(res.data.message || '热更新成功已生效！');
          emit('updated');
          emit('close');
        } else {
          toast.error(res.data?.message || res.message || '热更新失败');
          if (res.data?.rollbackPerformed) {
            toast.warning('新版本启动异常，系统已自动安全回滚至上一稳定版本。');
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
      if (!props.ext) return;
      const next = !props.ext.isEnabled;
      try {
        const res = await http(API.services.extensionSetEnabled(props.ext.manifest.id) + `?enabled=${next}`, {
          method: 'POST',
        });
        if (res.ok) {
          toast.success(`扩展已成功${next ? '启用' : '禁用'}`);
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
      updateResult,
      checkUpdate,
      executeHotUpdate,
      toggleEnabled,
    };
  },
  template: `
    <div v-if="show && ext" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-md w-full p-5 flex flex-col gap-3.5 border-cyan-500/40 animate-fade-in">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-2.5">
          <div class="flex items-center gap-2">
            <span class="text-lg">{{ ext.manifest.icon || '🔌' }}</span>
            <span class="font-display font-semibold text-slate-100 text-sm">扩展版本与溯源 - {{ ext.manifest.name }}</span>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">✕</button>
        </div>

        <div class="space-y-2.5 text-xs">
          <div class="flex items-center justify-between p-2 rounded bg-slate-900/60 border border-cyber-line/40">
            <span class="text-slate-400">运行状态</span>
            <span class="badge" :class="ext.isEnabled ? 'border-emerald-500/40 text-emerald-300' : 'border-slate-700 text-slate-500'">
              {{ ext.isEnabled ? '✓ 已启用' : '✕ 已禁用' }}
            </span>
          </div>

          <div class="flex items-center justify-between p-2 rounded bg-slate-900/60 border border-cyber-line/40">
            <span class="text-slate-400">本地已安装版本</span>
            <span class="font-mono text-cyan-300">v{{ ext.manifest.version }}</span>
          </div>

          <div class="flex items-center justify-between p-2 rounded bg-slate-900/60 border border-cyber-line/40">
            <span class="text-slate-400">远程最新版本</span>
            <span class="font-mono" :class="updateResult?.hasUpdate ? 'text-amber-300 font-bold' : 'text-slate-300'">
              {{ updateResult?.remoteVersion ? ('v' + updateResult.remoteVersion) : '未检测' }}
            </span>
          </div>

          <div v-if="ext.source" class="p-2.5 rounded bg-slate-900/60 border border-cyber-line/40 space-y-1">
            <div class="text-slate-400 text-[11px]">来源溯源 (source.json):</div>
            <div class="font-mono text-[11px] text-cyan-400 truncate">{{ ext.source.repoUrl || '系统内置/本地包' }}</div>
            <div v-if="ext.source.subDirectory" class="font-mono text-[10px] text-slate-500">子目录: {{ ext.source.subDirectory }}</div>
          </div>

          <div v-if="updateResult?.hasUpdate" class="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-xs flex items-center justify-between">
            <span>发现新版本 v{{ updateResult.remoteVersion }} 可用！</span>
            <button
              type="button"
              class="btn btn-xs border-amber-500/50 text-amber-300 hover:bg-amber-500/20"
              :disabled="updating"
              @click="executeHotUpdate()"
            >
              {{ updating ? '更新回滚保护中...' : '🚀 立即热更新' }}
            </button>
          </div>
        </div>

        <div class="flex items-center justify-between border-t border-cyber-line/40 pt-3 gap-2">
          <button
            type="button"
            class="btn btn-xs"
            :class="ext.isEnabled ? 'border-amber-600/40 text-amber-300' : 'border-emerald-600/40 text-emerald-300'"
            @click="toggleEnabled()"
          >
            {{ ext.isEnabled ? '🚫 禁用扩展' : '✓ 启用扩展' }}
          </button>

          <div class="flex items-center gap-1.5">
            <button
              type="button"
              class="btn btn-xs hover:border-cyan-500/40 text-cyan-300"
              :disabled="checking"
              @click="checkUpdate()"
            >
              {{ checking ? '检测中...' : '🔍 检查更新' }}
            </button>
            <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
