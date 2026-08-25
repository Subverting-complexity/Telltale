import type uPlot from 'uplot';

// Shared across every uPlot line chart (Timeline's ChartPanel and
// ProcessComparison's CompareChart) so they read as one visual system
// instead of two independently-tuned charts.

export const CHART_COLORS = {
  cpu: '#3b82f6',
  memory: '#10b981',
  disk: '#f59e0b',
  network: '#8b5cf6',
  io: '#ef4444',
};

export const COMPARE_COLORS = ['#3b82f6', '#10b981', '#f59e0b', '#ef4444'];

export interface ChartThemeColors {
  axes: string;
  grid: string;
  bg: string;
  thresholdLine: string;
  thresholdText: string;
  meanLine: string;
  meanText: string;
  trendLine: string;
}

export function getThemeColors(): ChartThemeColors {
  const isDark = document.documentElement.getAttribute('data-theme') === 'dark' ||
    (!document.documentElement.getAttribute('data-theme') &&
     window.matchMedia('(prefers-color-scheme: dark)').matches);

  return {
    axes: isDark ? '#9ca3af' : '#6b7280',
    grid: isDark ? '#374151' : '#e5e7eb',
    bg: isDark ? '#111827' : '#ffffff',
    thresholdLine: isDark ? 'rgba(156,163,175,0.4)' : 'rgba(107,114,128,0.3)',
    thresholdText: isDark ? '#9ca3af' : '#9ca3af',
    meanLine: isDark ? 'rgba(251,191,36,0.7)' : 'rgba(217,119,6,0.6)',
    meanText: isDark ? '#fbbf24' : '#d97706',
    trendLine: isDark ? 'rgba(244,114,182,0.6)' : 'rgba(219,39,119,0.5)',
  };
}

// Points show on every line, at every density. Size still scales down for
// dense series so a full day of sampled data doesn't turn into a solid bar
// of dots, but `show` itself is never gated on point count any more.
export function pointsConfig(pointCount: number, color: string): uPlot.Series.Points {
  return {
    show: pointCount > 0,
    size: pointCount <= 60 ? 6 : pointCount <= 400 ? 4 : 3,
    fill: color,
    stroke: color,
  };
}

function pad2(n: number): string {
  return n < 10 ? `0${n}` : `${n}`;
}

function formatClock(d: Date): string {
  let h = d.getHours();
  const m = d.getMinutes();
  const ampm = h >= 12 ? 'pm' : 'am';
  h = h % 12;
  if (h === 0) h = 12;
  return m === 0 ? `${h}${ampm}` : `${h}:${pad2(m)}${ampm}`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function formatDayMonth(d: Date): string {
  return `${MONTHS[d.getMonth()]} ${d.getDate()}`;
}

function formatMonthYear(d: Date): string {
  return `${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
}

const ONE_DAY = 24 * 3600;
const FORTY_FIVE_DAYS = 45 * ONE_DAY;

// uPlot's default time-axis formatter picks a label format from the pixel
// width available for each tick, so the same visible range can render
// differently depending on the container's current size, and the format
// can shift tick-to-tick as the range changes. Formatting off the total
// visible span instead of per-tick pixel space keeps the label format
// stable across day/week/month/year views and across resizes within one
// of those views.
export function timeAxisValues(u: uPlot, splits: number[]): string[] {
  const min = u.scales.x.min ?? splits[0] ?? 0;
  const max = u.scales.x.max ?? splits[splits.length - 1] ?? 0;
  const spanSeconds = Math.max(1, max - min);

  const format = spanSeconds <= ONE_DAY + 3600
    ? formatClock
    : spanSeconds <= FORTY_FIVE_DAYS
    ? formatDayMonth
    : formatMonthYear;

  return splits.map(ts => format(new Date(ts * 1000)));
}

export function buildAxes(
  theme: ChartThemeColors,
  yAxisValues?: uPlot.Axis['values'],
): [uPlot.Axis, uPlot.Axis] {
  return [
    {
      stroke: theme.axes,
      grid: { stroke: theme.grid, width: 1 },
      ticks: { stroke: theme.grid, width: 1 },
      values: timeAxisValues,
    },
    {
      stroke: theme.axes,
      grid: { stroke: theme.grid, width: 1 },
      ticks: { stroke: theme.grid, width: 1 },
      size: 60,
      values: yAxisValues,
    },
  ];
}
