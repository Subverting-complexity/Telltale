import { useMemo, useState, type CSSProperties } from 'react';
import type { TimelinePoint } from './types';
import { bucketSeries, clamp, computeMean, computePeak, formatRate, formatSizeGb } from './utils';
import type { MetricKey } from './palette';
import { metricCssVar } from './palette';
import { ReadingViewToggle } from './ReadingView';
import type { ReadingView } from './ReadingView';

interface HealthSummaryProps {
  timeline: TimelinePoint[];
  logicalProcessors: number;
  onScrollTo: (metric: 'cpu' | 'memory' | 'disk' | 'network') => void;
}

interface TileColorVars extends CSSProperties {
  '--tile-color': string;
}

/** How many points a sparkline is drawn from, in either view. */
const SPARKLINE_POINTS = 60;

// Same per-metric color as the sparkline and the line charts, rather than a
// green/amber/red health-status color — the sparkline already makes a busy
// metric visible through its own shape, so the bar doesn't need to repeat
// that signal in a second color scheme.
//
// It passes the palette's var() rather than a resolved value, so the tile
// follows a theme change without this component re-rendering.
function tileColorVars(metric: MetricKey): TileColorVars {
  return { '--tile-color': metricCssVar(metric) };
}

// Area fill plus an end-point dot, matching the line charts (chartTheme.ts's
// pointsConfig) instead of a bare stroke, so the tiles read as part of the
// same chart system rather than a smaller, plainer copy of it.
//
// stroke, fill and stop-color are set through style rather than as
// presentation attributes, because var() resolves in a CSS declaration and not
// in an SVG attribute value.
function Sparkline({ id, values, metric }: { id: string; values: (number | null)[]; metric: MetricKey }) {
  const color = metricCssVar(metric);
  const filtered = values.map(v => v ?? 0);
  if (filtered.length < 2) return null;
  const max = Math.max(...filtered, 1);
  const w = 80;
  const h = 22;
  const points = filtered.map((v, i): [number, number] => [
    (i / (filtered.length - 1)) * w,
    h - (v / max) * h,
  ]);
  const linePath = 'M' + points.map(p => p.join(',')).join(' L');
  const areaPath = `${linePath} L${w},${h} L0,${h} Z`;
  const [lastX, lastY] = points[points.length - 1];
  const gradientId = `sparkline-fill-${id}`;

  return (
    <svg className="sparkline" viewBox={`0 0 ${w} ${h}`} width={w} height={h} aria-hidden="true">
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" style={{ stopColor: color, stopOpacity: 0.32 }} />
          <stop offset="100%" style={{ stopColor: color, stopOpacity: 0 }} />
        </linearGradient>
      </defs>
      <path d={areaPath} fill={`url(#${gradientId})`} />
      <path d={linePath} fill="none" strokeWidth="1.4" style={{ stroke: color }} />
      <circle cx={lastX} cy={lastY} r="2" style={{ fill: color }} />
    </svg>
  );
}

interface TileProps {
  metric: MetricKey;
  header: string;
  value: string;
  /** Where the bar sits, as a percentage, or null for a tile that has no bar. */
  barPct: number | null;
  label: string | null;
  ariaLabel: string;
  spark: (number | null)[] | null;
  onClick: () => void;
}

function Tile({ metric, header, value, barPct, label, ariaLabel, spark, onClick }: TileProps) {
  return (
    <button className="health-tile" onClick={onClick} aria-label={ariaLabel} style={tileColorVars(metric)}>
      <div className="tile-header">{header}</div>
      <div className="tile-value">{value}</div>
      {barPct !== null && (
        <div className="tile-bar-track">
          <div className="tile-bar-fill" style={{ width: `${Math.min(barPct, 100)}%` }} />
        </div>
      )}
      {spark && <Sparkline id={metric} values={spark} metric={metric} />}
      {label && <div className="tile-label">{label}</div>}
    </button>
  );
}

/**
 * The series a tile's sparkline is drawn from.
 *
 * Now shows the trailing readings, because the number above it is the newest of
 * them and the line is there to say how it got there. Over time covers the whole
 * range, averaged down to the same width, so the line describes the range the
 * number is an average of rather than only its tail.
 */
function sparkSeries(values: (number | null)[], view: ReadingView): (number | null)[] {
  return view === 'now' ? values.slice(-SPARKLINE_POINTS) : bucketSeries(values, SPARKLINE_POINTS);
}

export function HealthSummary({ timeline, logicalProcessors, onScrollTo }: HealthSummaryProps) {
  const [view, setView] = useState<ReadingView>('now');

  const latest = timeline.length > 0 ? timeline[timeline.length - 1] : null;

  const cpuSeries = useMemo(() => timeline.map(p => p.cpuPct), [timeline]);
  const memSeries = useMemo(() => timeline.map(p => {
    if (p.memoryAvailMb == null || p.memoryTotalMb == null || p.memoryTotalMb <= 0) return null;
    return clamp(((p.memoryTotalMb - p.memoryAvailMb) / p.memoryTotalMb) * 100, 0, 100);
  }), [timeline]);
  const diskSeries = useMemo(() => timeline.map(p => p.diskBusyPct), [timeline]);
  const netSeries = useMemo(() => timeline.map(p => p.netKbps), [timeline]);

  if (!latest) return null;

  const overTime = view === 'over-time';
  // Named once so every tile says the same thing about which reading it is on,
  // in the accessible name as well as on screen.
  const whenNow = 'at the latest reading';
  const whenRange = 'across the range shown';

  const memTotalMb = latest.memoryTotalMb ?? 0;
  const memAvailMb = latest.memoryAvailMb;
  const memHasData = memAvailMb !== null && memAvailMb !== undefined && memTotalMb > 0;
  const memPctNow = memHasData ? clamp(((memTotalMb - memAvailMb!) / memTotalMb) * 100, 0, 100) : null;
  const memPctAvg = computeMean(memSeries);
  const memPctPeak = computePeak(memSeries);
  const memPct = overTime ? memPctAvg : memPctNow;
  const memUsedMb = memPct !== null ? (memPct / 100) * memTotalMb : null;
  const memPeakUsedMb = memPctPeak !== null ? (memPctPeak / 100) * memTotalMb : null;

  const cpuPct = (overTime ? computeMean(cpuSeries) : latest.cpuPct) ?? 0;
  const cpuPeak = computePeak(cpuSeries) ?? 0;
  const diskPct = (overTime ? computeMean(diskSeries) : latest.diskBusyPct) ?? 0;
  const diskPeak = computePeak(diskSeries) ?? 0;
  const netKbps = overTime ? computeMean(netSeries) : latest.netKbps;
  const netPeak = computePeak(netSeries);

  const diskText = (pct: number) => (pct < 1 ? 'Idle' : `${pct.toFixed(1)}%`);

  return (
    <section className="health-summary" aria-label="System health summary">
      <div className="health-summary-header">
        <p className="health-summary-caption">
          {overTime ? 'Averages across the range shown' : 'The latest reading'}
        </p>
        <ReadingViewToggle value={view} onChange={setView} label="System health reading" />
      </div>

      <div className="health-tiles">
        <Tile
          metric="cpu"
          header="CPU"
          value={`${cpuPct.toFixed(0)}%`}
          barPct={cpuPct}
          label={overTime ? `Peak ${cpuPeak.toFixed(0)}% · ${logicalProcessors} cores` : `${logicalProcessors} cores`}
          ariaLabel={overTime
            ? `CPU: average ${cpuPct.toFixed(0)}%, peak ${cpuPeak.toFixed(0)}%, of ${logicalProcessors} cores ${whenRange}`
            : `CPU: ${cpuPct.toFixed(0)}% of ${logicalProcessors} cores ${whenNow}`}
          spark={sparkSeries(cpuSeries, view)}
          onClick={() => onScrollTo('cpu')}
        />

        <Tile
          metric="memory"
          header="Memory"
          value={memPct !== null ? `${memPct.toFixed(0)}%` : '-'}
          barPct={memPct}
          label={memPct === null || memUsedMb === null
            ? (memTotalMb > 0 ? formatSizeGb(memTotalMb) : 'No data')
            : overTime && memPeakUsedMb !== null
              ? `Peak ${formatSizeGb(memPeakUsedMb)} / ${formatSizeGb(memTotalMb)}`
              : `${formatSizeGb(memUsedMb)} / ${formatSizeGb(memTotalMb)}`}
          ariaLabel={memPct === null || memUsedMb === null
            ? 'Memory: no data'
            : overTime && memPeakUsedMb !== null
              ? `Memory: average ${formatSizeGb(memUsedMb)} of ${formatSizeGb(memTotalMb)} (${memPct.toFixed(0)}%), peak ${formatSizeGb(memPeakUsedMb)}, ${whenRange}`
              : `Memory: ${formatSizeGb(memUsedMb)} / ${formatSizeGb(memTotalMb)} (${memPct.toFixed(0)}%) ${whenNow}`}
          spark={memPct !== null ? sparkSeries(memSeries, view) : null}
          onClick={() => onScrollTo('memory')}
        />

        <Tile
          metric="disk"
          header="Disk"
          value={diskText(diskPct)}
          barPct={diskPct}
          label={overTime ? `Peak ${diskText(diskPeak)}` : diskPct >= 1 ? 'busy' : null}
          ariaLabel={overTime
            ? `Disk: average ${diskText(diskPct)} busy, peak ${diskText(diskPeak)}, ${whenRange}`
            : `Disk: ${diskPct < 1 ? 'Idle' : `${diskPct.toFixed(1)}% busy`} ${whenNow}`}
          spark={sparkSeries(diskSeries, view)}
          onClick={() => onScrollTo('disk')}
        />

        <Tile
          metric="network"
          header="Network"
          value={formatRate(netKbps)}
          barPct={null}
          label={overTime ? `Peak ${formatRate(netPeak)}` : null}
          ariaLabel={overTime
            ? `Network: average ${formatRate(netKbps)}, peak ${formatRate(netPeak)}, ${whenRange}`
            : `Network: ${formatRate(netKbps)} ${whenNow}`}
          spark={sparkSeries(netSeries, view)}
          onClick={() => onScrollTo('network')}
        />
      </div>
    </section>
  );
}
