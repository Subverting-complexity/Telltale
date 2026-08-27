import type { ViewState } from './types';

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

/**
 * Heading for a CPU figure exactly as the collector recorded it: processor time
 * used over time elapsed, as a share of a single core. A process spread across
 * four cores reads 400%.
 *
 * The two headings below exist because the dashboard shows both kinds side by
 * side, and a percentage that does not say what it is a percentage of cannot be
 * compared with the one next to it.
 */
export const CPU_OF_ONE_CORE = 'CPU % of one core';

/**
 * Heading for a CPU figure divided by the core count of the machine that was
 * recorded, so it is a share of everything that machine can do and stops at
 * 100%. This is the scale the machine gauge has always used.
 */
export const CPU_OF_ALL_CORES = 'CPU % of all cores';

/**
 * Converts a stored per core CPU figure into a share of the whole machine.
 *
 * `logicalProcessors` is the core count of the machine the recording was made
 * on, which the viewer reads from the recording itself. A count below one cannot
 * be divided by sensibly, so the figure is left on its recorded scale rather
 * than turned into something larger than the truth.
 */
export function formatCpuOfAllCores(pct: number | null, logicalProcessors: number): string {
  if (pct === null) return '-';

  // Written as a negated lower bound so a NaN core count falls back too. A
  // straight `< 1` is false for NaN, which would divide and render "NaN%".
  if (!(logicalProcessors >= 1)) return formatCpu(pct);

  return formatCpu(pct / logicalProcessors);
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

/**
 * The day a wipe would offer to delete, or null when the view spans more than
 * one day.
 *
 * A wipe is offered per day and never per month or year, so a view that is not
 * on a single day has no day to name, and the control says so rather than
 * quietly deleting something wider than the label suggests.
 */
export function viewedDay(view: ViewState): { label: string; from: number; to: number } | null {
  if (view.scale !== 'day' || view.month === undefined || view.day === undefined) return null;

  const { from, to } = getDayRange(view.year, view.month, view.day);
  const label = new Date(from).toLocaleDateString(undefined, {
    weekday: 'long', year: 'numeric', month: 'long', day: 'numeric',
  });
  return { label, from, to };
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

export type ProcessCategory = 'system' | 'services' | 'applications';

const SYSTEM_PROCESS_NAMES = new Set([
  'system', 'idle', 'registry', 'csrss.exe', 'lsass.exe', 'smss.exe',
  'wininit.exe', 'services.exe', 'svchost.exe', 'dwm.exe', 'conhost.exe',
  'winlogon.exe', 'fontdrvhost.exe', 'lsaiso.exe', 'memory compression',
  'secure system', 'ntoskrnl.exe', 'spoolsv.exe', 'dllhost.exe',
  'sihost.exe', 'taskhostw.exe', 'runtimebroker.exe', 'searchhost.exe',
  'startmenuexperiencehost.exe', 'textinputhost.exe', 'shellexperiencehost.exe',
  'explorer.exe', 'ctfmon.exe', 'audiodg.exe',
]);

const SYSTEM_PATH_PREFIXES = [
  'c:\\windows\\system32\\',
  'c:\\windows\\syswow64\\',
  'c:\\windows\\',
];

const SERVICE_PATH_PREFIXES = [
  'c:\\program files\\',
  'c:\\program files (x86)\\',
  'c:\\programdata\\',
];

export function categoriseProcess(name: string, path: string | null): ProcessCategory {
  const lowerName = name.toLowerCase();
  if (SYSTEM_PROCESS_NAMES.has(lowerName)) return 'system';

  if (path) {
    const lowerPath = path.toLowerCase();
    if (SYSTEM_PATH_PREFIXES.some(p => lowerPath.startsWith(p))) return 'system';
    if (SERVICE_PATH_PREFIXES.some(p => lowerPath.startsWith(p))) return 'services';
  }

  return 'applications';
}

export function formatRate(kbps: number | null): string {
  if (kbps === null) return '-';
  if (kbps >= 1048576) return `${(kbps / 1048576).toFixed(1)} GB/s`;
  if (kbps >= 1024) return `${(kbps / 1024).toFixed(1)} MB/s`;
  return `${kbps.toFixed(0)} KB/s`;
}

export function formatMemoryPercent(availMb: number | null, totalMb: number | null): string {
  if (availMb === null || totalMb === null || totalMb === 0) return '-';
  const usedPct = ((totalMb - availMb) / totalMb) * 100;
  return `${usedPct.toFixed(1)}%`;
}

export function formatSizeGb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb.toFixed(0)} MB`;
}

export function computeMovingAverage(values: (number | null)[], windowSize: number): (number | null)[] {
  if (windowSize < 1) return values.map(() => null);
  const result: (number | null)[] = new Array(values.length);
  for (let i = 0; i < values.length; i++) {
    const start = Math.max(0, i - Math.floor(windowSize / 2));
    const end = Math.min(values.length, start + windowSize);
    let sum = 0;
    let count = 0;
    for (let j = start; j < end; j++) {
      if (values[j] !== null) {
        sum += values[j]!;
        count++;
      }
    }
    result[i] = count > 0 ? sum / count : null;
  }
  return result;
}

export function computeLinearFit(values: (number | null)[]): { slope: number; intercept: number } | null {
  let n = 0, sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
  for (let i = 0; i < values.length; i++) {
    if (values[i] !== null) {
      const y = values[i]!;
      n++;
      sumX += i;
      sumY += y;
      sumXY += i * y;
      sumXX += i * i;
    }
  }
  if (n < 2) return null;
  const denom = n * sumXX - sumX * sumX;
  if (denom === 0) return null;
  const slope = (n * sumXY - sumX * sumY) / denom;
  const intercept = (sumY - slope * sumX) / n;
  return { slope, intercept };
}

/**
 * The largest recorded value, or null when nothing was recorded.
 *
 * The guard is `!= null` rather than `!== null` so an undefined entry is skipped
 * as a missing reading. A strict check would take undefined as the running peak,
 * and every later comparison against it is false, so one undefined at the front
 * would report no peak for a series full of readings.
 */
export function computePeak(values: (number | null)[]): number | null {
  let peak: number | null = null;
  for (const v of values) {
    if (v != null && (peak === null || v > peak)) peak = v;
  }
  return peak;
}

/**
 * Averages a series down to at most `buckets` points, for a sparkline that has
 * to describe a whole range rather than its tail.
 *
 * Averaging rather than sampling every nth reading, because a sparkline drawn
 * from samples of a day misses the spikes between them and reports a calm
 * machine. A bucket with nothing recorded in it stays null, so a gap in the
 * recording stays a gap instead of being drawn as zero.
 *
 * A series already short enough is returned as it is, not stretched.
 */
export function bucketSeries(values: (number | null)[], buckets: number): (number | null)[] {
  if (buckets < 1) return [];
  if (values.length <= buckets) return values;

  const result: (number | null)[] = new Array(buckets);
  for (let i = 0; i < buckets; i++) {
    // Bounds are computed from the index rather than by stepping a fixed width,
    // so a length that does not divide evenly spreads the remainder across the
    // buckets instead of piling it all into the last one.
    const start = Math.floor((i * values.length) / buckets);
    const end = Math.floor(((i + 1) * values.length) / buckets);
    result[i] = computeMean(values.slice(start, Math.max(end, start + 1)));
  }
  return result;
}

export function computeMean(values: (number | null)[]): number | null {
  let sum = 0;
  let count = 0;
  for (const v of values) {
    if (v !== null) {
      sum += v;
      count++;
    }
  }
  return count > 0 ? sum / count : null;
}
