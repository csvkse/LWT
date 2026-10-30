import { defineComponent } from 'vue';

// 宿主环境画像与特权穿透引导条
export default defineComponent({
  name: 'EnvironmentBanner',
  props: {
    env: { type: Object, default: () => ({}) },
    loading: { type: Boolean, default: false },
  },
  emits: ['refresh'],
  setup(props, { emit }) {
    function onRefresh() {
      emit('refresh');
    }

    return { onRefresh };
  },
  template: `
    <div class="panel p-3.5 flex flex-col gap-2.5 bg-gradient-to-r from-slate-900/90 via-slate-900/70 to-cyan-950/20 border-cyan-500/20">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <!-- 主机与操作系统信息 -->
        <div class="flex items-center gap-3">
          <div class="w-9 h-9 rounded-lg bg-cyan-500/10 border border-cyan-500/30 flex items-center justify-center text-lg shrink-0">
            {{ env.operatingSystem === 'Windows' ? '🪟' : (env.operatingSystem === 'Linux' ? '🐧' : '🍎') }}
          </div>
          <div>
            <div class="flex items-center gap-2">
              <span class="font-display font-semibold text-slate-100 text-sm tracking-wide">{{ env.hostname || 'LocalHost' }}</span>
              <span class="badge border-cyan-500/40 text-cyan-300 text-[10px]">{{ env.operatingSystem }} ({{ env.is64Bit ? '64-bit' : '32-bit' }})</span>
              <span v-if="env.isDockerContainer" class="badge" :class="env.isPrivilegedContainer ? 'border-emerald-500/40 text-emerald-300' : 'border-amber-500/40 text-amber-300'">
                {{ env.isPrivilegedContainer ? 'Docker 特权容器 (PID1穿透)' : 'Docker 普通容器' }}
              </span>
              <span v-else class="badge border-slate-600/60 text-slate-400">物理裸机/虚机</span>
            </div>
            <div class="text-xs text-slate-400 mt-0.5 truncate max-w-[28rem]" :title="env.platform || env.kernelVersion">
              内核: {{ env.kernelVersion || '未知' }} · 协议栈: {{ env.networkStack || '标准网络' }}
            </div>
          </div>
        </div>

        <!-- 关键基础设施状态徽标与刷新按钮 -->
        <div class="flex flex-wrap items-center gap-2 ml-auto">
          <!-- Systemd 状态 -->
          <span class="badge py-1 px-2" :class="env.hasSystemd ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-300' : 'border-slate-700 bg-slate-900 text-slate-500'">
            ⚙️ Systemd {{ env.systemdPierced ? '(宿主穿透)' : (env.hasSystemd ? '(原生)' : '(不可用)') }}
          </span>

          <!-- Docker Daemon 状态 -->
          <span class="badge py-1 px-2" :class="env.hasDockerSocket ? 'border-cyan-500/30 bg-cyan-500/10 text-cyan-300' : 'border-slate-700 bg-slate-900 text-slate-500'">
            🐳 Docker {{ env.hasDockerSocket ? '在线' : '未挂载' }}
          </span>

          <!-- 刷新按钮 -->
          <button
            type="button"
            class="btn btn-xs hover:border-cyan-500/40"
            :disabled="loading"
            @click="onRefresh()"
            title="重新探测环境"
          >
            🔄 刷新环境
          </button>
        </div>
      </div>

      <!-- Docker 容器普通模式引导提示（未挂载特权或 Socket） -->
      <div v-if="env.isDockerContainer && (!env.isPrivilegedContainer || !env.hasDockerSocket)" class="bg-amber-500/10 border border-amber-500/30 rounded p-2.5 text-xs text-amber-300/90 flex items-start gap-2">
        <span class="text-base shrink-0">💡</span>
        <div class="space-y-1">
          <div class="font-medium text-amber-200">宿主穿透提示：</div>
          <div v-if="!env.hasDockerSocket">
            • 若需在容器内管理宿主机 Docker，请在启动时挂载 Unix Socket:
            <code class="px-1 py-0.5 rounded bg-black/40 font-mono text-[11px] text-cyan-300">-v /var/run/docker.sock:/var/run/docker.sock</code>
          </div>
          <div v-if="!env.isPrivilegedContainer">
            • 若需在容器内透明管理宿主机 systemctl 服务，请开启特权并共享 PID 命名空间:
            <code class="px-1 py-0.5 rounded bg-black/40 font-mono text-[11px] text-cyan-300">--privileged --pid=host</code>
          </div>
        </div>
      </div>
    </div>
  `,
});
