import { defineComponent } from 'vue';
import { modalState, closeModal } from '../store/modal.js';

export default defineComponent({
  name: 'ModalHost',
  data() {
    return { modalState };
  },
  methods: {
    closeModal,
    async confirm() {
      const current = modalState.current;
      if (!current) return;
      closeModal();
      if (typeof current.onConfirm === 'function') {
        await current.onConfirm();
      }
    },
  },
  template: `
    <div v-if="modalState.current" class="fixed inset-0 z-[90] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
      <div class="panel w-full max-w-md p-5" style="background: rgba(13, 21, 38, 0.97)">
        <h3 class="font-display text-base mb-3" :class="modalState.current.danger ? 'text-rose-300' : 'text-neon-soft'">
          {{ modalState.current.title }}
        </h3>
        <p class="text-sm text-slate-300 whitespace-pre-wrap break-all leading-relaxed">{{ modalState.current.message }}</p>
        <div class="flex justify-end gap-2 mt-5">
          <button class="btn" @click="closeModal()">取消</button>
          <button :class="modalState.current.danger ? 'btn btn-danger' : 'btn btn-primary'" @click="confirm()">
            {{ modalState.current.confirmText }}
          </button>
        </div>
      </div>
    </div>
  `,
});
