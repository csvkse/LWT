import { defineComponent } from 'vue';
import { toastState, dismiss } from '../store/toast.js';

const TYPE_CLASS = {
  success: 'border-emerald-500/50 text-emerald-300',
  error: 'border-rose-500/50 text-rose-300',
  info: 'border-cyan-500/50 text-cyan-300',
};

const TYPE_ICON = { success: '✓', error: '✕', info: 'ℹ' };

export default defineComponent({
  name: 'ToastHost',
  data() {
    return { toastState };
  },
  methods: {
    dismiss,
    iconOf(type) {
      return TYPE_ICON[type] || TYPE_ICON.info;
    },
    classOf(type) {
      return TYPE_CLASS[type] || TYPE_CLASS.info;
    },
  },
  template: `
    <div class="fixed top-4 right-4 z-[100] flex flex-col gap-2 w-80 max-w-[90vw]">
      <div
        v-for="item in toastState.items"
        :key="item.id"
        class="px-4 py-2.5 flex items-start gap-2 text-sm shadow-lg backdrop-blur rounded-xl"
        :class="classOf(item.type)"
        style="background: rgba(13, 21, 38, 0.94)"
      >
        <span class="mt-0.5">{{ iconOf(item.type) }}</span>
        <span class="flex-1 break-all text-slate-300">{{ item.message }}</span>
        <button class="text-slate-500 hover:text-slate-300" @click="dismiss(item.id)">✕</button>
      </div>
    </div>
  `,
});
