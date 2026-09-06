import { reactive } from 'vue';

export const toastState = reactive({ items: [] });

let seed = 0;

function push(type, message, duration) {
  const id = ++seed;
  toastState.items.push({ id, type, message });
  window.setTimeout(() => dismiss(id), duration);
}

export function dismiss(id) {
  const index = toastState.items.findIndex((item) => item.id === id);
  if (index >= 0) toastState.items.splice(index, 1);
}

export const toast = {
  success: (message) => push('success', message, 2800),
  error: (message) => push('error', message, 4200),
  info: (message) => push('info', message, 3200),
};
