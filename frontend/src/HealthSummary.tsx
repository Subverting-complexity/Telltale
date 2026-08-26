import { useMemo } from 'react';
import type { TimelinePoint } from './types';
import { formatRate, formatSizeGb } from './utils';

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

function Sparkline({ values }: { values: (number | null)[] }) {
  const filtered = values.map(v => v ?? 0);
  if (filtered.length < 2) return null;
  const max = Math.max(...filtered, 1);
  const w = 80;
  const h = 24;
  const points = filtered.map((v, i) =>
    `${(i / (filtered.length - 1)) * w},${h - (v / max) * h}`
  ).join(' ');

  return (
    <svg className="sparkline" viewBox={`0 0 ${w} ${h}`} width={w} height={h}
         aria-hidden="true">
      <polyline points={points} fill="none" stroke="var(--accent)" strokeWidth="1.5" />
    </svg>
  );
}

export function HealthSummary({ timeline, logicalProcessors, onScrollTo }: HealthSummaryProps) {
  const latest = timeline.length > 0 ? timeline[timeline.length - 1] : null;
  const sparklineData = useMemo(() => {
    const slice = timeline.slice(-60);
    return slice.map(p => p.netKbps);
  }, [timeline]);

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
        {diskPct >= 1 && <div className="tile-label">busy</div>}
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('network')}
        aria-label={`Network: ${formatRate(netKbps)}`}
      >
        <div className="tile-header">Network</div>
        <div className="tile-value">{formatRate(netKbps)}</div>
        <Sparkline values={sparklineData} />
      </button>
    </section>
  );
}
