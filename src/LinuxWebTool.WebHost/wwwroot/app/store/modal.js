import { reactive } from 'vue';

export const modalState = reactive({ current: null });

export function openConfirm({ title = '确认操作', message = '', confirmText = '确认', danger = false, onConfirm }) {
  modalState.current = { title, message, confirmText, danger, onConfirm };
}

export function closeModal() {
  modalState.current = null;
}
