export function formatSize(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  if (mb >= 1) return `${mb.toFixed(1)} MB`;
  return `${(mb * 1024).toFixed(0)} KB`;
}

export function formatDate(ts: number): string {
  return new Date(ts).toLocaleDateString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric',
  });
}

export function formatTime(ts: number): string {
  return new Date(ts).toLocaleTimeString(undefined, {
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

export function formatDateTime(ts: number): string {
  return `${formatDate(ts)} ${formatTime(ts)}`;
}

export function formatElapsed(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

export function formatCpu(pct: number | null): string {
  if (pct === null) return '-';
  if (pct >= 100) return `${pct.toFixed(0)}%`;
  if (pct >= 10) return `${pct.toFixed(1)}%`;
  return `${pct.toFixed(2)}%`;
}

export function formatIo(kb: number | null): string {
  if (kb === null) return '-';
  if (kb >= 1048576) return `${(kb / 1048576).toFixed(1)} GB`;
  if (kb >= 1024) return `${(kb / 1024).toFixed(1)} MB`;
  return `${kb.toFixed(0)} KB`;
}

export function getDayRange(year: number, month: number, day: number): { from: number; to: number } {
  const start = new Date(year, month - 1, day);
  const end = new Date(year, month - 1, day + 1);
  return { from: start.getTime(), to: end.getTime() - 1 };
}

export function getMonthRange(year: number, month: number): { from: number; to: number } {
  const start = new Date(year, month - 1, 1);
  const end = new Date(year, month, 1);
  return { from: start.getTime(), to: end.getTime() - 1 };
}

export function getWeekRange(year: number, month: number, day: number): { from: number; to: number } {
  const d = new Date(year, month - 1, day);
  const dayOfWeek = d.getDay();
  const start = new Date(year, month - 1, day - dayOfWeek);
  const end = new Date(start.getFullYear(), start.getMonth(), start.getDate() + 7);
  return { from: start.getTime(), to: end.getTime() - 1 };
}

export function getYearRange(year: number): { from: number; to: number } {
  const start = new Date(year, 0, 1);
  const end = new Date(year + 1, 0, 1);
  return { from: start.getTime(), to: end.getTime() - 1 };
}

export function getDaysInMonth(year: number, month: number): number {
  return new Date(year, month, 0).getDate();
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
