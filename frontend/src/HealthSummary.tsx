import { useMemo } from 'react';
import type { TimelinePoint } from './types';
import { formatRate, formatSizeGb } from './utils';
import { CHART_COLORS } from './chartTheme';

interface HealthSummaryProps {
  timeline: TimelinePoint[];
  logicalProcessors: number;
  onScrollTo: (metric: 'cpu' | 'memory' | 'disk' | 'network') => void;
}

function getZoneClass(pct: number): string {
  if (pct >= 80) return 'zone-danger';
  if (pct >= 50) return 'zone-warning';
  return 'zone-ok';
}

// Area fill plus an end-point dot, matching the line charts (chartTheme.ts's
// pointsConfig) instead of a bare stroke, so the tiles read as part of the
// same chart system rather than a smaller, plainer copy of it.
function Sparkline({ id, values, color }: { id: string; values: (number | null)[]; color: string }) {
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
          <stop offset="0%" stopColor={color} stopOpacity="0.32" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={areaPath} fill={`url(#${gradientId})`} />
      <path d={linePath} fill="none" stroke={color} strokeWidth="1.4" />
      <circle cx={lastX} cy={lastY} r="2" fill={color} />
    </svg>
  );
}

export function HealthSummary({ timeline, logicalProcessors, onScrollTo }: HealthSummaryProps) {
  const latest = timeline.length > 0 ? timeline[timeline.length - 1] : null;

  const cpuSeries = useMemo(() => timeline.slice(-60).map(p => p.cpuPct), [timeline]);
  const memSeries = useMemo(() => timeline.slice(-60).map(p => {
    if (p.memoryAvailMb == null || p.memoryTotalMb == null || p.memoryTotalMb <= 0) return null;
    return Math.min(((p.memoryTotalMb - p.memoryAvailMb) / p.memoryTotalMb) * 100, 100);
  }), [timeline]);
  const diskSeries = useMemo(() => timeline.slice(-60).map(p => p.diskBusyPct), [timeline]);
  const netSeries = useMemo(() => timeline.slice(-60).map(p => p.netKbps), [timeline]);

  if (!latest) return null;

  const cpuPct = latest.cpuPct ?? 0;
  const memTotalMb = latest.memoryTotalMb ?? 0;
  const memAvailMb = latest.memoryAvailMb;
  const memHasData = memAvailMb !== null && memAvailMb !== undefined && memTotalMb > 0;
  const memUsedMb = memHasData ? Math.max(0, memTotalMb - memAvailMb) : null;
  const memPct = memHasData && memUsedMb !== null ? Math.min((memUsedMb / memTotalMb) * 100, 100) : null;
  const diskPct = latest.diskBusyPct ?? 0;
  const netKbps = latest.netKbps;

  return (
    <section className="health-summary" aria-label="System health summary">
      <button
        className="health-tile"
        onClick={() => onScrollTo('cpu')}
        aria-label={`CPU: ${cpuPct.toFixed(0)}% of ${logicalProcessors} cores`}
      >
        <div className="tile-header">CPU</div>
        <div className="tile-value">{cpuPct.toFixed(0)}%</div>
        <div className="tile-bar-track">
          <div
            className={`tile-bar-fill ${getZoneClass(cpuPct)}`}
            style={{ width: `${Math.min(cpuPct, 100)}%` }}
          />
        </div>
        <Sparkline id="cpu" values={cpuSeries} color={CHART_COLORS.cpu} />
        <div className="tile-label">{logicalProcessors} cores</div>
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('memory')}
        aria-label={memPct !== null
          ? `Memory: ${formatSizeGb(memUsedMb!)} / ${formatSizeGb(memTotalMb)} (${memPct.toFixed(0)}%)`
          : `Memory: no data`}
      >
        <div className="tile-header">Memory</div>
        <div className="tile-value">{memPct !== null ? `${memPct.toFixed(0)}%` : '-'}</div>
        {memPct !== null ? (
          <>
            <div className="tile-bar-track">
              <div
                className={`tile-bar-fill ${getZoneClass(memPct)}`}
                style={{ width: `${Math.min(memPct, 100)}%` }}
              />
            </div>
            <Sparkline id="memory" values={memSeries} color={CHART_COLORS.memory} />
            <div className="tile-label">{formatSizeGb(memUsedMb!)} / {formatSizeGb(memTotalMb)}</div>
          </>
        ) : (
          <div className="tile-label">{memTotalMb > 0 ? formatSizeGb(memTotalMb) : 'No data'}</div>
        )}
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('disk')}
        aria-label={`Disk: ${diskPct < 1 ? 'Idle' : `${diskPct.toFixed(1)}% busy`}`}
      >
        <div className="tile-header">Disk</div>
        <div className="tile-value">{diskPct < 1 ? 'Idle' : `${diskPct.toFixed(1)}%`}</div>
        <div className="tile-bar-track">
          <div
            className={`tile-bar-fill ${getZoneClass(diskPct)}`}
            style={{ width: `${Math.min(diskPct, 100)}%` }}
          />
        </div>
        <Sparkline id="disk" values={diskSeries} color={CHART_COLORS.disk} />
        {diskPct >= 1 && <div className="tile-label">busy</div>}
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('network')}
        aria-label={`Network: ${formatRate(netKbps)}`}
      >
        <div className="tile-header">Network</div>
        <div className="tile-value">{formatRate(netKbps)}</div>
        <Sparkline id="network" values={netSeries} color={CHART_COLORS.network} />
      </button>
    </section>
  );
}
