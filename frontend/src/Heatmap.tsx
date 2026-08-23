import { useEffect, useState, useMemo, Fragment } from 'react';
import { getHeatmap } from './api';
import type { HeatmapBucket } from './types';
import { formatSize, formatRate } from './utils';

interface HeatmapViewProps {
  from: number;
  to: number;
  onNavigateToDay: (year: number, month: number, day: number, hour?: number) => void;
}

type HeatmapMetric = 'cpu' | 'memory' | 'disk' | 'network';

const METRICS: { key: HeatmapMetric; label: string; hue: number; format: (v: number) => string }[] = [
  { key: 'cpu', label: 'CPU %', hue: 220, format: v => `${v.toFixed(1)}%` },
  { key: 'memory', label: 'Memory', hue: 150, format: v => formatSize(v) },
  { key: 'disk', label: 'Disk %', hue: 40, format: v => `${v.toFixed(1)}%` },
  { key: 'network', label: 'Network', hue: 270, format: v => formatRate(v) },
];

function heatColor(value: number, max: number, hue: number): string {
  if (max === 0 || value === 0) return 'var(--bg-tertiary)';
  const intensity = Math.min(value / max, 1);
  const lightness = 90 - intensity * 50;
  const saturation = 50 + intensity * 40;
  return `hsl(${hue}, ${saturation}%, ${lightness}%)`;
}

function HeatmapGrid({ buckets, hue, days, format, from, onNavigateToDay }: {
  buckets: HeatmapBucket[];
  hue: number;
  days: number;
  format: (v: number) => string;
  from: number;
  onNavigateToDay: (year: number, month: number, day: number, hour?: number) => void;
}) {
  const grid = useMemo(() => {
    const cells: Map<string, HeatmapBucket> = new Map();
    let maxVal = 0;
    for (const b of buckets) {
      cells.set(`${b.dayOffset}-${b.hour}`, b);
      if (b.avg > maxVal) maxVal = b.avg;
    }
    return { cells, maxVal };
  }, [buckets]);

  const [tooltip, setTooltip] = useState<{ x: number; y: number; text: string } | null>(null);

  return (
    <div className="heatmap-grid-wrapper">
      <div
        className="heatmap-grid"
        style={{
          display: 'grid',
          gridTemplateColumns: `40px repeat(${days}, 1fr)`,
          gridTemplateRows: `24px repeat(24, 1fr)`,
          gap: '1px',
          minHeight: 200,
        }}
        onMouseLeave={() => setTooltip(null)}
      >
        {/* Header row - day labels */}
        <div className="heatmap-corner" />
        {Array.from({ length: days }, (_, d) => {
          const date = new Date(from + d * 86400000);
          return (
            <div key={`h-${d}`} className="heatmap-day-label">
              {date.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' })}
            </div>
          );
        })}

        {/* Hour rows */}
        {Array.from({ length: 24 }, (_, h) => (
          <Fragment key={`row-${h}`}>
            <div className="heatmap-hour-label">{String(h).padStart(2, '0')}:00</div>
            {Array.from({ length: days }, (_, d) => {
              const cell = grid.cells.get(`${d}-${h}`);
              const value = cell?.avg ?? 0;
              const bg = heatColor(value, grid.maxVal, hue);

              return (
                <button
                  key={`${d}-${h}`}
                  className="heatmap-cell"
                  style={{ backgroundColor: bg }}
                  onClick={() => {
                    const date = new Date(from + d * 86400000);
                    onNavigateToDay(date.getFullYear(), date.getMonth() + 1, date.getDate(), h);
                  }}
                  onMouseEnter={(e) => {
                    const date = new Date(from + d * 86400000);
                    const dateStr = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
                    setTooltip({
                      x: e.clientX,
                      y: e.clientY,
                      text: `${dateStr} ${String(h).padStart(2, '0')}:00\nAvg: ${format(value)}${cell ? `\nPeak: ${format(cell.peak)}` : ''}`,
                    });
                  }}
                  aria-label={`${format(value)}`}
                />
              );
            })}
          </Fragment>
        ))}
      </div>

      {tooltip && (
        <div
          className="heatmap-tooltip"
          style={{ position: 'fixed', left: tooltip.x + 10, top: tooltip.y - 10 }}
        >
          {tooltip.text.split('\n').map((line, i) => <div key={i}>{line}</div>)}
        </div>
      )}
    </div>
  );
}

export function HeatmapView({ from, to, onNavigateToDay }: HeatmapViewProps) {
  const [data, setData] = useState<Map<string, HeatmapBucket[]>>(new Map());
  const [loading, setLoading] = useState(true);
  const [activeMetric, setActiveMetric] = useState<HeatmapMetric | 'all'>('all');

  const days = Math.max(1, Math.ceil((to - from) / 86400000));

  useEffect(() => {
    setLoading(true);
    Promise.all(
      METRICS.map(m =>
        getHeatmap(from, to, m.key)
          .then(res => [m.key, res.buckets] as const)
          .catch(() => [m.key, []] as const)
      )
    ).then(results => {
      const map = new Map<string, HeatmapBucket[]>();
      for (const [key, buckets] of results) {
        map.set(key, buckets);
      }
      setData(map);
      setLoading(false);
    });
  }, [from, to]);

  if (loading) return <p className="loading">Loading heatmap data...</p>;

  const metricsToShow = activeMetric === 'all'
    ? METRICS
    : METRICS.filter(m => m.key === activeMetric);

  return (
    <div className="heatmap-view">
      <div className="heatmap-controls">
        <button
          className={`toggle-btn ${activeMetric === 'all' ? 'active' : ''}`}
          onClick={() => setActiveMetric('all')}
        >
          All
        </button>
        {METRICS.map(m => (
          <button
            key={m.key}
            className={`toggle-btn ${activeMetric === m.key ? 'active' : ''}`}
            onClick={() => setActiveMetric(m.key)}
          >
            {m.label}
          </button>
        ))}
      </div>

      {metricsToShow.map(m => (
        <div key={m.key} className="heatmap-section">
          <h4 className="heatmap-title">{m.label}</h4>
          <HeatmapGrid
            buckets={data.get(m.key) ?? []}
            hue={m.hue}
            days={days}
            format={m.format}
            from={from}
            onNavigateToDay={onNavigateToDay}
          />
        </div>
      ))}
    </div>
  );
}
