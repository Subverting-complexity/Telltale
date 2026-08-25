import { useState, useMemo } from 'react';
import type { ProcessGroupRow } from './types';
import { formatSize, formatIo, categoriseProcess, formatCpuOfAllCores } from './utils';
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
    const withoutIdle = processes.filter(p => p.name.toLowerCase() !== 'idle');
    if (categoryFilter === 'all') return withoutIdle;
    return withoutIdle.filter(p => categoriseProcess(p.name, p.path) === categoryFilter);
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
      const label = metric === 'cpu' ? formatCpuOfAllCores(p.cpuPct, logicalProcessors)
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

  const metricLabel = metric === 'cpu' ? 'CPU, as a share of all cores'
    : metric === 'memory' ? 'memory usage' : 'I/O activity';

  return (
    <section className="top-consumers" aria-label="Top resource consumers">
      <div className="top-consumers-header">
        <div>
          <h2>Top Consumers</h2>
          <p className="top-consumers-subtitle">Processes ranked by {metricLabel}</p>
        </div>
        <div className="metric-toggle-group" role="radiogroup" aria-label="Metric">
          {(['cpu', 'memory', 'io'] as const).map(m => (
            <button
              key={m}
              className={`metric-toggle ${metric === m ? 'active' : ''}`}
              onClick={() => setMetric(m)}
              role="radio"
              aria-checked={metric === m}
            >
              {m === 'cpu' ? 'CPU' : m === 'memory' ? 'Memory' : 'I/O'}
            </button>
          ))}
        </div>
      </div>

      <div className="consumer-list" role="list">
        {items.map((item, idx) => (
          <button
            key={item.name}
            className="consumer-row"
            onClick={() => onSelectProcess(item.name)}
            role="listitem"
            aria-label={`${item.name}: ${item.label}`}
          >
            <span className="consumer-rank">{idx + 1}</span>
            <span className="consumer-name">{item.name}</span>
            <div className="consumer-bar-track">
              <div
                className="consumer-bar-fill"
                style={{
                  width: `${Math.min(item.pct, 100)}%`,
                  background: `linear-gradient(90deg, ${item.color}, ${item.color}cc)`,
                  boxShadow: `0 0 8px ${item.color}40`,
                }}
              />
            </div>
            <span className="consumer-value">{item.label}</span>
          </button>
        ))}
      </div>
    </section>
  );
}
