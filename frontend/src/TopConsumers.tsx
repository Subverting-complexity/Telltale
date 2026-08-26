import { useState, useMemo, type CSSProperties } from 'react';
import type { ProcessGroupRow } from './types';
import { formatSize, formatIo, categoriseProcess, formatCpuOfAllCores } from './utils';
import type { ProcessCategory } from './utils';
import { metricCssVar } from './palette';

interface TopConsumersProps {
  processes: ProcessGroupRow[];
  logicalProcessors: number;
  onSelectProcess: (name: string) => void;
  categoryFilter: ProcessCategory | 'all';
}

const MAX_ITEMS = 8;

type MetricView = 'cpu' | 'memory' | 'io';

interface MetricColorVars extends CSSProperties {
  '--metric-color': string;
}

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

    return sorted.map(p => {
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
      };
    });
  }, [filtered, metric, logicalProcessors]);

  if (filtered.length === 0) return null;

  const metricLabel = metric === 'cpu' ? 'CPU, as a share of all cores'
    : metric === 'memory' ? 'memory usage' : 'I/O activity';

  // Reuses the palette's per-metric color instead of the panel picking its
  // own, so switching CPU / Memory / I/O recolors the whole panel to match
  // the same line the System Overview chart draws for that metric. The var()
  // rather than a resolved value, so it also follows a theme change.
  const metricColorVars: MetricColorVars = { '--metric-color': metricCssVar(metric) };

  return (
    <section className="top-consumers" aria-label="Top resource consumers" style={metricColorVars}>
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
            <span className="consumer-wash" style={{ width: `${Math.min(item.pct, 100)}%` }} aria-hidden="true" />
            <span className="consumer-tick" aria-hidden="true" />
            <span className="consumer-rank">{idx + 1}</span>
            <span className="consumer-name">{item.name}</span>
            <span className="consumer-value">{item.label}</span>
          </button>
        ))}
      </div>
    </section>
  );
}
