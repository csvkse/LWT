import { defineComponent } from 'vue';

export default defineComponent({
  name: 'ExtensionConfigModal',
  props: {
    show: { type: Boolean, default: false },
    ext: { type: Object, default: () => ({}) },
    formValues: { type: Object, default: () => ({}) },
    saving: { type: Boolean, default: false },
  },
  emits: ['close', 'save'],
  setup(props, { emit }) {
    function closeModal() {
      emit('close');
    }

    function save() {
      emit('save');
    }

    return {
      closeModal,
      save,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div class="panel max-w-md w-full p-5 flex flex-col gap-4 border-cyan-500/40 animate-fade-in max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between border-b border-cyber-line/50 pb-3">
          <div class="font-display font-semibold text-slate-100 text-sm">
            配置参数: {{ ext?.manifest?.name }}
          </div>
          <button type="button" class="btn btn-xs hover:border-slate-500" @click="closeModal()">✕</button>
        </div>
        <div class="space-y-3">
          <div v-for="field in ext?.manifest?.configForm" :key="field.field" class="space-y-1">
            <label class="text-xs text-slate-300 flex items-center justify-between">
              <span>{{ field.label }}</span>
              <span v-if="field.required" class="text-rose-400 text-[10px]">*必填</span>
            </label>
            <input
              v-if="field.type === 'number'"
              v-model="formValues[field.field]"
              type="number"
              class="input text-xs w-full font-mono"
            />
            <input
              v-else-if="field.type === 'password'"
              v-model="formValues[field.field]"
              type="password"
              class="input text-xs w-full font-mono"
            />
            <input
              v-else
              v-model="formValues[field.field]"
              type="text"
              class="input text-xs w-full font-mono"
            />
          </div>
        </div>
        <div class="flex justify-end gap-2 pt-2">
          <button type="button" class="btn btn-xs" @click="closeModal()">取消</button>
          <button type="button" class="btn btn-xs btn-primary" :disabled="saving" @click="save()">
            {{ saving ? '正在保存...' : '保存配置' }}
          </button>
        </div>
      </div>
    </div>
  `,
});
