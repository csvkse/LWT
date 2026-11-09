import { computed, defineComponent } from 'vue';

export default defineComponent({
  name: 'PluginCardMetaBadges',
  props: {
    plugin: {
      type: Object,
      required: true,
    },
  },
  setup(props) {
    // 生命周期 Lifecycle
    const lifecycleMeta = computed(() => {
      const build = props.plugin.manifest?.build;
      const strategy = build?.strategy || (build?.buildCommand ? 'SourceCompiled' : null);
      if (strategy === 'SourceCompiled' || build?.buildCommand) {
        return {
          label: '源码构建',
          icon: '🔨',
          badgeClass: 'bg-purple-500/10 border-purple-500/30 text-purple-300',
          title: '支持热重载与在线源码构建',
        };
      }
      if (strategy === 'Precompiled' || props.plugin.manifest?.entry?.endsWith('.so') || props.plugin.manifest?.entry?.endsWith('.wasm')) {
        return {
          label: '预编译',
          icon: '📦',
          badgeClass: 'bg-sky-500/10 border-sky-500/30 text-sky-300',
          title: '预构建 Native ELF / Wasm 二进制模块',
        };
      }
      return {
        label: '解释执行',
        icon: '⚡',
        badgeClass: 'bg-emerald-500/10 border-emerald-500/30 text-emerald-300',
        title: '脚本解释执行模式',
      };
    });

    // 协议 Protocol
    const protocolMeta = computed(() => {
      const iface = props.plugin.manifest?.interface;
      const proto = iface?.protocol || (props.plugin.profile === 3 ? 'WasmAbi' : (props.plugin.manifest?.network?.port ? 'Http' : 'Cli'));
      switch (proto) {
        case 'Http':
        case 'HTTP':
          return {
            label: 'HTTP',
            icon: '🌐',
            badgeClass: 'bg-cyan-500/10 border-cyan-500/30 text-cyan-300',
            detail: props.plugin.allocatedPort ? `:${props.plugin.allocatedPort}` : '',
          };
        case 'Uds':
        case 'UDS':
          return {
            label: 'UDS',
            icon: '🔌',
            badgeClass: 'bg-amber-500/10 border-amber-500/30 text-amber-300',
            detail: 'IPC',
          };
        case 'WasmAbi':
          return {
            label: 'WasmAbi',
            icon: '⚡',
            badgeClass: 'bg-fuchsia-500/10 border-fuchsia-500/30 text-fuchsia-300',
            detail: 'In-Proc',
          };
        case 'Cli':
        default:
          return {
            label: 'CLI',
            icon: '💻',
            badgeClass: 'bg-slate-800 border-slate-700 text-slate-300',
            detail: '',
          };
      }
    });

    const isServiceOrFunction = computed(() => props.plugin.profile === 0 || props.plugin.profile === 2 || props.plugin.profile === 3);

    return {
      lifecycleMeta,
      protocolMeta,
      isServiceOrFunction,
    };
  },
  template: `
    <div class="flex items-center gap-1.5 flex-wrap">
      <!-- 生命周期徽标 -->
      <span
        class="text-[10px] px-1.5 py-0.5 rounded border flex items-center gap-1 shrink-0 select-none font-mono"
        :class="lifecycleMeta.badgeClass"
        :title="lifecycleMeta.title"
      >
        <span>{{ lifecycleMeta.icon }}</span>
        <span>{{ lifecycleMeta.label }}</span>
      </span>

      <!-- 接入协议徽标 -->
      <span
        class="text-[10px] px-1.5 py-0.5 rounded border flex items-center gap-1 shrink-0 select-none font-mono"
        :class="protocolMeta.badgeClass"
        :title="'接入协议: ' + protocolMeta.label"
      >
        <span>{{ protocolMeta.icon }}</span>
        <span>{{ protocolMeta.label }}</span>
        <span v-if="protocolMeta.detail" class="opacity-80">{{ protocolMeta.detail }}</span>
      </span>
    </div>
  `,
});
