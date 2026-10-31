import { defineComponent } from 'vue';

export default defineComponent({
  name: 'ExtensionLogsModal',
  props: {
    show: { type: Boolean, default: false },
    extId: { type: String, default: '' },
    logs: { type: String, default: '' },
  },
  emits: ['close'],
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-3xl w-full p-5 flex flex-col gap-3 border-cyan-500/40 animate-fade-in max-h-[85vh]">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-2.5">
          <div class="font-display font-semibold text-slate-100 text-sm">
            运行日志: <span class="font-mono text-cyan-400">{{ extId }}</span>
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">✕</button>
        </div>
        <pre class="p-3 bg-cyber-bg/95 rounded border border-cyber-line/70 font-mono text-xs text-slate-300 flex-1 overflow-y-auto whitespace-pre-wrap select-all leading-relaxed">{{ logs }}</pre>
        <div class="flex justify-end">
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="$emit('close')">关闭</button>
        </div>
      </div>
    </div>
  `,
});
