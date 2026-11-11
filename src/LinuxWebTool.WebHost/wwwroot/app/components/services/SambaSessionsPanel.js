import { defineComponent } from 'vue';

export default defineComponent({
  name: 'SambaSessionsPanel',
  props: {
    sessions: { type: Array, default: () => [] },
    loading: { type: Boolean, default: false },
  },
  emits: ['refresh'],
  setup() {
    return {};
  },
  template: `
    <div>
      <div v-if="loading" class="panel p-8 text-center text-slate-500 text-xs">
        正在读取局域网 SMB 活跃会话与连接状态 (smbstatus)...
      </div>
      <div v-else-if="sessions.length === 0" class="panel p-8 text-center text-slate-500 text-xs">
        当前暂无局域网客户端正在连接 Samba 共享 (smbstatus 监测正常)。
      </div>
      <div v-else class="panel overflow-x-auto list-viewport">
        <table class="data-table min-w-[48rem]">
          <thead>
            <tr>
              <th>服务 PID</th>
              <th>认证用户</th>
              <th>客户端机器 / IP</th>
              <th>挂载共享</th>
              <th>SMB 协议版本</th>
              <th>连接建立时间</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="s in sessions" :key="s.pid + '_' + s.shareName">
              <td class="font-mono text-xs text-slate-400">
                #{{ s.pid }}
              </td>
              <td class="font-semibold text-xs text-cyan-300">
                👤 {{ s.username }}
              </td>
              <td class="font-mono text-xs text-slate-200">
                {{ s.clientIp || s.machine || '-' }}
              </td>
              <td>
                <span class="badge border-cyan-500/40 text-cyan-300 text-xs">
                  [{{ s.shareName || 'IPC$' }}]
                </span>
              </td>
              <td>
                <span class="badge text-[11px] border-emerald-500/40 text-emerald-300">
                  {{ s.protocolVersion || 'SMB3_11' }}
                </span>
              </td>
              <td class="text-xs text-slate-400">
                {{ s.connectedAt || '近期活跃' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
});
