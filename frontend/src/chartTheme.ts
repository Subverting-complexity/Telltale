import type uPlot from 'uplot';
import type { MetricKey, ThemeMode } from './palette';
import { METRIC_KEYS, chartColor, metricColor } from './palette';

// Shared across every uPlot line chart (Timeline's ChartPanel and
// ProcessComparison's CompareChart) so they read as one visual system
// instead of two independently-tuned charts.
//
// No colour is chosen here any more. uPlot paints onto a canvas and cannot
// read a CSS variable, so this file resolves palette.ts's values for the
// mode it is given; everything that renders DOM or SVG uses the var() instead
// and needs none of this.

// Comparison series are assigned metric colours in declaration order. The
// palette has five, so five processes can be compared before two of them share
// a colour, rather than the four the old hard-coded list allowed.
export function compareColors(mode: ThemeMode): string[] {
  return METRIC_KEYS.map(key => metricColor(key, mode));
}

export function seriesColor(key: MetricKey, mode: ThemeMode): string {
  return metricColor(key, mode);
}

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

// Takes the mode rather than reading the document, so a caller has to hold the
// mode as a value it can depend on. That is what makes a theme switch repaint
// the charts: useThemeMode feeds this, and the chart rebuild depends on it.
export function getThemeColors(mode: ThemeMode): ChartThemeColors {
  return {
    axes: chartColor('chart-axis', mode),
    grid: chartColor('chart-grid', mode),
    bg: chartColor('chart-bg', mode),
    thresholdLine: chartColor('chart-threshold-line', mode),
    thresholdText: chartColor('chart-threshold-text', mode),
    meanLine: chartColor('chart-mean-line', mode),
    meanText: chartColor('chart-mean-text', mode),
    trendLine: chartColor('chart-trend-line', mode),
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
