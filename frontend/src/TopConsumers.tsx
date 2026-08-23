import { useState, useMemo } from 'react';
import type { ProcessGroupRow } from './types';
import { formatSize, formatIo, categoriseProcess } from './utils';
import type { ProcessCategory } from './utils';

interface TopConsumersProps {
  processes: ProcessGroupRow[];
  logicalProcessors: number;
  onSelectProcess: (name: string) => void;
  categoryFilter: ProcessCategory | 'all';
}

const PROCESS_COLORS = [
  '#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6',
  '#06b6d4', '#f97316', '#ec4899', '#84cc16', '#6366f1',
];

const MAX_SEGMENTS = 8;

type ViewMode = 'bar' | 'treemap';
type TreemapMetric = 'cpu' | 'memory';

interface SegmentData {
  name: string;
  value: number;
  pct: number;
  color: string;
  label: string;
}

function getSegments(
  items: { name: string; value: number }[],
  formatter: (v: number) => string,
): SegmentData[] {
  const total = items.reduce((sum, item) => sum + item.value, 0);
  if (total === 0) return [];

  const sorted = [...items].sort((a, b) => b.value - a.value);
  const top = sorted.slice(0, MAX_SEGMENTS);
  const otherValue = sorted.slice(MAX_SEGMENTS).reduce((sum, item) => sum + item.value, 0);

  const segments: SegmentData[] = top.map((item, i) => ({
    name: item.name,
    value: item.value,
    pct: (item.value / total) * 100,
    color: PROCESS_COLORS[i % PROCESS_COLORS.length],
    label: formatter(item.value),
  }));

  if (otherValue > 0) {
    segments.push({
      name: 'Other',
      value: otherValue,
      pct: (otherValue / total) * 100,
      color: '#9ca3af',
      label: formatter(otherValue),
    });
  }

  return segments;
}

function StackedBar({ title, segments, onSelect }: {
  title: string;
  segments: SegmentData[];
  onSelect: (name: string) => void;
}) {
  if (segments.length === 0) return null;

  return (
    <div className="stacked-bar-row">
      <span className="stacked-bar-label">{title}</span>
      <div className="stacked-bar" role="img" aria-label={`${title} usage by process`}>
        {segments.map(seg => (
          <button
            key={seg.name}
            className="stacked-segment"
            style={{ width: `${Math.max(seg.pct, 0.5)}%`, backgroundColor: seg.color }}
            onClick={(e) => { e.stopPropagation(); if (seg.name !== 'Other') onSelect(seg.name); }}
            title={`${seg.name}: ${seg.label} (${seg.pct.toFixed(1)}%)`}
            aria-label={`${seg.name}: ${seg.label}`}
            disabled={seg.name === 'Other'}
          />
        ))}
      </div>
    </div>
  );
}

function squarify(items: { name: string; value: number; color: string; label: string }[], width: number, height: number) {
  const total = items.reduce((sum, item) => sum + item.value, 0);
  if (total === 0) return [];

  const rects: { name: string; x: number; y: number; w: number; h: number; color: string; label: string; value: number }[] = [];
  let x = 0, y = 0, remainingW = width, remainingH = height;

  const sorted = [...items].sort((a, b) => b.value - a.value);
  let remaining = total;

  for (let i = 0; i < sorted.length; i++) {
    const item = sorted[i];
    const ratio = item.value / remaining;

    if (remainingW >= remainingH) {
      const w = remainingW * ratio;
      rects.push({ name: item.name, x, y, w, h: remainingH, color: item.color, label: item.label, value: item.value });
      x += w;
      remainingW -= w;
    } else {
      const h = remainingH * ratio;
      rects.push({ name: item.name, x, y, w: remainingW, h, color: item.color, label: item.label, value: item.value });
      y += h;
      remainingH -= h;
    }
    remaining -= item.value;
  }

  return rects;
}

export function TopConsumers({ processes, logicalProcessors, onSelectProcess, categoryFilter }: TopConsumersProps) {
  const [viewMode, setViewMode] = useState<ViewMode>('bar');
  const [treemapMetric, setTreemapMetric] = useState<TreemapMetric>('cpu');

  const filtered = useMemo(() => {
    if (categoryFilter === 'all') return processes;
    return processes.filter(p => categoriseProcess(p.name, p.path) === categoryFilter);
  }, [processes, categoryFilter]);

  const cpuSegments = useMemo(() =>
    getSegments(
      filtered.map(p => ({ name: p.name, value: p.cpuPct / logicalProcessors })),
      v => `${v.toFixed(1)}%`
    ), [filtered, logicalProcessors]);

  const memSegments = useMemo(() =>
    getSegments(
      filtered.map(p => ({ name: p.name, value: p.privateMb })),
      v => formatSize(v)
    ), [filtered]);

  const ioSegments = useMemo(() =>
    getSegments(
      filtered.map(p => ({ name: p.name, value: p.ioKb })),
      v => formatIo(v)
    ), [filtered]);

  const treemapItems = useMemo(() => {
    const sorted = [...filtered].sort((a, b) => {
      const av = treemapMetric === 'cpu' ? a.cpuPct : a.privateMb;
      const bv = treemapMetric === 'cpu' ? b.cpuPct : b.privateMb;
      return bv - av;
    }).slice(0, 12);

    return sorted.map((p, i) => ({
      name: p.name,
      value: treemapMetric === 'cpu' ? p.cpuPct : p.privateMb,
      color: PROCESS_COLORS[i % PROCESS_COLORS.length],
      label: treemapMetric === 'cpu'
        ? `${(p.cpuPct / logicalProcessors).toFixed(1)}%`
        : formatSize(p.privateMb),
    }));
  }, [filtered, treemapMetric, logicalProcessors]);

  // Build legend from CPU segments (they share colors)
  const legend = cpuSegments.filter(s => s.name !== 'Other');

  if (filtered.length === 0) return null;

  return (
    <section className="top-consumers" aria-label="Top resource consumers">
      <div className="top-consumers-header">
        <h2>Top Consumers</h2>
        <div className="top-consumers-toggles">
          <button
            className={`toggle-btn ${viewMode === 'bar' ? 'active' : ''}`}
            onClick={() => setViewMode('bar')}
            aria-pressed={viewMode === 'bar'}
          >
            Bar
          </button>
          <button
            className={`toggle-btn ${viewMode === 'treemap' ? 'active' : ''}`}
            onClick={() => setViewMode('treemap')}
            aria-pressed={viewMode === 'treemap'}
          >
            Treemap
          </button>
        </div>
      </div>

      {viewMode === 'bar' ? (
        <div className="stacked-bars">
          <StackedBar title="CPU" segments={cpuSegments} onSelect={onSelectProcess} />
          <StackedBar title="Memory" segments={memSegments} onSelect={onSelectProcess} />
          <StackedBar title="I/O" segments={ioSegments} onSelect={onSelectProcess} />

          <div className="stacked-legend">
            {legend.map(seg => (
              <span key={seg.name} className="legend-item">
                <span className="legend-dot" style={{ backgroundColor: seg.color }} />
                {seg.name}
              </span>
            ))}
          </div>
        </div>
      ) : (
        <div className="treemap-section">
          <div className="treemap-metric-toggle">
            <button
              className={`toggle-btn ${treemapMetric === 'cpu' ? 'active' : ''}`}
              onClick={() => setTreemapMetric('cpu')}
            >
              CPU
            </button>
            <button
              className={`toggle-btn ${treemapMetric === 'memory' ? 'active' : ''}`}
              onClick={() => setTreemapMetric('memory')}
            >
              Memory
            </button>
          </div>
          <div className="treemap-container" style={{ position: 'relative', width: '100%', height: 240 }}>
            {squarify(treemapItems, 100, 100).map(rect => (
              <button
                key={rect.name}
                className="treemap-rect"
                style={{
                  position: 'absolute',
                  left: `${rect.x}%`,
                  top: `${rect.y}%`,
                  width: `${rect.w}%`,
                  height: `${rect.h}%`,
                  backgroundColor: rect.color,
                }}
                onClick={() => onSelectProcess(rect.name)}
                title={`${rect.name}: ${rect.label}`}
                aria-label={`${rect.name}: ${rect.label}`}
              >
                {rect.w > 8 && rect.h > 15 && (
                  <span className="treemap-label">
                    <span className="treemap-name">{rect.name}</span>
                    <span className="treemap-value">{rect.label}</span>
                  </span>
                )}
              </button>
            ))}
          </div>
        </div>
      )}
    </section>
  );
}
