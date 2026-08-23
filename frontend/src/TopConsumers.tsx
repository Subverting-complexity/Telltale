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

const MAX_ITEMS = 8;

type MetricView = 'cpu' | 'memory' | 'io';

export function TopConsumers({ processes, logicalProcessors, onSelectProcess, categoryFilter }: TopConsumersProps) {
  const [metric, setMetric] = useState<MetricView>('cpu');

  const filtered = useMemo(() => {
    if (categoryFilter === 'all') return processes;
    return processes.filter(p => categoriseProcess(p.name, p.path) === categoryFilter);
  }, [processes, categoryFilter]);

  const items = useMemo(() => {
    const sorted = [...filtered].sort((a, b) => {
      if (metric === 'cpu') return b.cpuPct - a.cpuPct;
      if (metric === 'memory') return b.privateMb - a.privateMb;
      return b.ioKb - a.ioKb;
    }).slice(0, MAX_ITEMS);

    const maxVal = sorted.length > 0
      ? metric === 'cpu' ? sorted[0].cpuPct / logicalProcessors
      : metric === 'memory' ? sorted[0].privateMb
      : sorted[0].ioKb
      : 1;

    return sorted.map((p, i) => {
      const value = metric === 'cpu' ? p.cpuPct / logicalProcessors
        : metric === 'memory' ? p.privateMb
        : p.ioKb;
      const label = metric === 'cpu' ? `${value.toFixed(1)}%`
        : metric === 'memory' ? formatSize(p.privateMb)
        : formatIo(p.ioKb);
      return {
        name: p.name,
        value,
        label,
        pct: maxVal > 0 ? (value / maxVal) * 100 : 0,
        color: PROCESS_COLORS[i % PROCESS_COLORS.length],
      };
    });
  }, [filtered, metric, logicalProcessors]);

  if (filtered.length === 0) return null;

  const metricLabel = metric === 'cpu' ? 'CPU usage' : metric === 'memory' ? 'memory usage' : 'I/O activity';

  return (
    <section className="top-consumers" aria-label="Top resource consumers">
      <div className="top-consumers-header">
        <h2>Top Consumers</h2>
        <div className="top-consumers-toggles">
          <button
            className={`toggle-btn ${metric === 'cpu' ? 'active' : ''}`}
            onClick={() => setMetric('cpu')}
            aria-pressed={metric === 'cpu'}
          >
            CPU
          </button>
          <button
            className={`toggle-btn ${metric === 'memory' ? 'active' : ''}`}
            onClick={() => setMetric('memory')}
            aria-pressed={metric === 'memory'}
          >
            Memory
          </button>
          <button
            className={`toggle-btn ${metric === 'io' ? 'active' : ''}`}
            onClick={() => setMetric('io')}
            aria-pressed={metric === 'io'}
          >
            I/O
          </button>
        </div>
      </div>

      <p className="top-consumers-subtitle">Processes ranked by {metricLabel}</p>

      <div className="consumer-list" role="list">
        {items.map(item => (
          <button
            key={item.name}
            className="consumer-row"
            onClick={() => onSelectProcess(item.name)}
            role="listitem"
            aria-label={`${item.name}: ${item.label}`}
          >
            <span className="consumer-name">{item.name}</span>
            <div className="consumer-bar-track">
              <div
                className="consumer-bar-fill"
                style={{ width: `${Math.min(item.pct, 100)}%`, backgroundColor: item.color }}
              />
            </div>
            <span className="consumer-value">{item.label}</span>
          </button>
        ))}
      </div>
    </section>
  );
}
