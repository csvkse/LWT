import { defineComponent, ref } from 'vue';

export default defineComponent({
  name: 'NativeCommandEditor',
  props: {
    command: { type: Object, default: () => ({}) },
    readonly: { type: Boolean, default: false },
  },
  emits: ['update:command'],
  setup(props, { emit }) {
    const activeTab = ref('general'); // general | arguments | stdin | artifacts

    return {
      activeTab,
    };
  },
  template: `
    <div class="space-y-3 text-xs bg-slate-900/50 p-3.5 rounded border border-cyber-line/50">
      <!-- 选项卡切换 -->
      <div class="flex items-center gap-2 border-b border-cyber-line/40 pb-2">
        <button
          type="button"
          class="px-2.5 py-1 rounded text-xs transition-colors"
          :class="activeTab === 'general' ? 'bg-cyan-500/20 text-cyan-300 font-semibold border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'general'"
        >
          基本配置
        </button>
        <button
          type="button"
          class="px-2.5 py-1 rounded text-xs transition-colors"
          :class="activeTab === 'arguments' ? 'bg-cyan-500/20 text-cyan-300 font-semibold border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'arguments'"
        >
          参数绑定 ({{ command?.argumentBindings?.length || 0 }})
        </button>
        <button
          type="button"
          class="px-2.5 py-1 rounded text-xs transition-colors"
          :class="activeTab === 'stdin' ? 'bg-cyan-500/20 text-cyan-300 font-semibold border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'stdin'"
        >
          标准输入 ({{ command?.stdinBinding?.mode || 'closed' }})
        </button>
        <button
          type="button"
          class="px-2.5 py-1 rounded text-xs transition-colors"
          :class="activeTab === 'artifacts' ? 'bg-cyan-500/20 text-cyan-300 font-semibold border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
          @click="activeTab = 'artifacts'"
        >
          产物规则 ({{ command?.artifactRules?.length || 0 }})
        </button>
      </div>

      <!-- 基本配置 Tab -->
      <div v-if="activeTab === 'general'" class="space-y-2.5">
        <div class="grid grid-cols-2 gap-3">
          <div>
            <label class="text-slate-400 block mb-1">程序目标 (ProgramRef):</label>
            <input
              type="text"
              class="input w-full font-mono text-xs"
              :value="command?.programRef"
              :disabled="readonly"
              readonly
            />
          </div>
          <div>
            <label class="text-slate-400 block mb-1">执行超时 (秒):</label>
            <input
              type="number"
              class="input w-full font-mono text-xs"
              :value="command?.timeoutSeconds || 30"
              :disabled="readonly"
              readonly
            />
          </div>
        </div>

        <div class="grid grid-cols-2 gap-3">
          <div>
            <label class="text-slate-400 block mb-1">结果解析器 (ResultParser):</label>
            <input
              type="text"
              class="input w-full font-mono text-xs"
              :value="command?.resultParser || 'bounded-text'"
              :disabled="readonly"
              readonly
            />
          </div>
          <div>
            <label class="text-slate-400 block mb-1">进度解析器 (ProgressParser):</label>
            <input
              type="text"
              class="input w-full font-mono text-xs"
              :value="command?.progressParser || 'none'"
              :disabled="readonly"
              readonly
            />
          </div>
        </div>
      </div>

      <!-- 参数绑定 Tab -->
      <div v-if="activeTab === 'arguments'" class="space-y-2">
        <div v-if="!command?.argumentBindings?.length" class="text-slate-500 italic py-2">
          未定义参数绑定（将以空参数运行）。
        </div>
        <div v-else class="space-y-1.5 max-h-48 overflow-y-auto">
          <div
            v-for="(b, idx) in command.argumentBindings"
            :key="idx"
            class="flex items-center gap-2 p-1.5 rounded bg-slate-800/60 border border-cyber-line/30 font-mono text-[11px]"
          >
            <span class="text-slate-500 w-5 text-right">{{ idx + 1 }}.</span>
            <span class="badge badge-xs border-cyan-500/40 text-cyan-300">{{ b.type }}</span>
            <span v-if="b.prefix" class="text-amber-400">{{ b.prefix }}</span>
            <span class="text-slate-200">{{ b.value }}</span>
            <span v-if="b.conditionField" class="text-slate-500 text-[10px] ml-auto">
              [if: {{ b.conditionField }}]
            </span>
          </div>
        </div>
      </div>

      <!-- 标准输入 Tab -->
      <div v-if="activeTab === 'stdin'" class="space-y-2">
        <div class="grid grid-cols-2 gap-3">
          <div>
            <label class="text-slate-400 block mb-1">Stdin 模式:</label>
            <span class="font-mono text-cyan-300 font-semibold">{{ command?.stdinBinding?.mode || 'closed' }}</span>
          </div>
          <div v-if="command?.stdinBinding?.sourceField">
            <label class="text-slate-400 block mb-1">数据源字段 (SourceField):</label>
            <span class="font-mono text-slate-200">{{ command.stdinBinding.sourceField }}</span>
          </div>
        </div>
      </div>

      <!-- 产物规则 Tab -->
      <div v-if="activeTab === 'artifacts'" class="space-y-2">
        <div v-if="!command?.artifactRules?.length" class="text-slate-500 italic py-2">
          未定义产物抓取规则。
        </div>
        <div v-else class="space-y-1.5 max-h-48 overflow-y-auto">
          <div
            v-for="(rule, idx) in command.artifactRules"
            :key="idx"
            class="flex items-center justify-between p-2 rounded bg-slate-800/60 border border-cyber-line/30 text-[11px]"
          >
            <div class="flex items-center gap-2 font-mono">
              <span class="text-cyan-300 font-bold">#{{ rule.artifactId }}</span>
              <span class="text-slate-400">→</span>
              <span class="text-slate-200">{{ rule.relativePath }}</span>
            </div>
            <div class="flex items-center gap-2">
              <span v-if="rule.contentType" class="text-[10px] text-slate-500">{{ rule.contentType }}</span>
              <span v-if="rule.optional" class="badge badge-xs border-slate-600 text-slate-400">可选</span>
              <span v-else class="badge badge-xs border-emerald-500/40 text-emerald-300">必需</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
