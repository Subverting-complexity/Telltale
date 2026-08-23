import { useMemo } from 'react';
import type { TimelinePoint } from './types';
import { formatRate } from './utils';

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

function formatGb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb.toFixed(0)} MB`;
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
  const memAvailMb = latest.memoryAvailMb ?? 0;
  const memUsedMb = memTotalMb - memAvailMb;
  const memPct = memTotalMb > 0 ? (memUsedMb / memTotalMb) * 100 : 0;
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
        <div className="tile-bar-track">
          <div
            className={`tile-bar-fill ${getZoneClass(cpuPct)}`}
            style={{ width: `${Math.min(cpuPct, 100)}%` }}
          />
        </div>
        <div className="tile-label">{cpuPct.toFixed(0)}% of {logicalProcessors} cores</div>
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('memory')}
        aria-label={`Memory: ${formatGb(memUsedMb)} / ${formatGb(memTotalMb)} (${memPct.toFixed(0)}%)`}
      >
        <div className="tile-header">Memory</div>
        <div className="tile-bar-track">
          <div
            className={`tile-bar-fill ${getZoneClass(memPct)}`}
            style={{ width: `${Math.min(memPct, 100)}%` }}
          />
        </div>
        <div className="tile-label">{formatGb(memUsedMb)} / {formatGb(memTotalMb)} ({memPct.toFixed(0)}%)</div>
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('disk')}
        aria-label={`Disk: ${diskPct < 5 ? 'Idle' : `${diskPct.toFixed(0)}% busy`}`}
      >
        <div className="tile-header">Disk</div>
        {diskPct >= 5 ? (
          <>
            <div className="tile-bar-track">
              <div
                className={`tile-bar-fill ${getZoneClass(diskPct)}`}
                style={{ width: `${Math.min(diskPct, 100)}%` }}
              />
            </div>
            <div className="tile-label">{diskPct.toFixed(0)}% busy</div>
          </>
        ) : (
          <div className="tile-label tile-idle">{diskPct < 1 ? 'Idle' : `${diskPct.toFixed(0)}% busy`}</div>
        )}
      </button>

      <button
        className="health-tile"
        onClick={() => onScrollTo('network')}
        aria-label={`Network: ${formatRate(netKbps)}`}
      >
        <div className="tile-header">Network</div>
        <Sparkline values={sparklineData} />
        <div className="tile-label">{formatRate(netKbps)}</div>
      </button>
    </section>
  );
}
