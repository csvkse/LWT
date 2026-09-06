// 展示格式化工具
export function formatTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

export function formatDuration(ms) {
  if (ms === null || ms === undefined) return '—';
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(2)} s`;
}

export function formatBytes(bytes) {
  if (bytes === null || bytes === undefined || Number.isNaN(Number(bytes))) return '—';
  let value = Number(bytes);
  if (Math.abs(value) < 1024) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB', 'PB'];
  let unit = -1;
  do {
    value /= 1024;
    unit++;
  } while (Math.abs(value) >= 1024 && unit < units.length - 1);
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

export function formatBps(bytesPerSec) {
  return formatBytes(bytesPerSec) + '/s';
}

export function formatUptime(seconds) {
  if (!seconds || seconds < 0) return '—';
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return `${days} 天 ${hours} 小时`;
  if (hours > 0) return `${hours} 小时 ${minutes} 分`;
  return `${minutes} 分钟`;
}

// 使用率对应的进度条颜色 class
export function usageColor(percent) {
  if (percent >= 85) return { bar: 'bg-rose-500', text: 'text-rose-300' };
  if (percent >= 60) return { bar: 'bg-amber-400', text: 'text-amber-300' };
  return { bar: 'bg-emerald-400', text: 'text-emerald-300' };
}

export const SOURCE_META = {
  0: { label: '手动', class: 'border-cyan-500/50 text-cyan-300' },
  1: { label: '定时', class: 'border-violet-500/50 text-violet-300' },
  2: { label: '快速', class: 'border-amber-500/50 text-amber-300' },
};

export const STATUS_META = {
  0: { label: '成功', class: 'border-emerald-500/50 text-emerald-300' },
  1: { label: '失败', class: 'border-rose-500/50 text-rose-300' },
  2: { label: '超时', class: 'border-orange-500/50 text-orange-300' },
  3: { label: '取消', class: 'border-slate-500/50 text-slate-400' },
};

export const SOURCE_OPTIONS = [
  { value: '', label: '全部来源' },
  { value: 0, label: '手动' },
  { value: 1, label: '定时' },
  { value: 2, label: '快速' },
];

export const STATUS_OPTIONS = [
  { value: '', label: '全部状态' },
  { value: 0, label: '成功' },
  { value: 1, label: '失败' },
  { value: 2, label: '超时' },
  { value: 3, label: '取消' },
];

export function sourceMeta(source) {
  return SOURCE_META[source] || { label: '未知', class: 'border-slate-500/50 text-slate-400' };
}

export function statusMeta(status) {
  return STATUS_META[status] || { label: '未知', class: 'border-slate-500/50 text-slate-400' };
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
