import { useState, useMemo, type CSSProperties } from 'react';
import type { ProcessGroupRow } from './types';
import { formatSize, formatIo, categoriseProcess, formatCpuOfAllCores, formatTime } from './utils';
import type { ProcessCategory } from './utils';
import { metricCssVar } from './palette';
import { ReadingViewToggle } from './ReadingView';
import type { ReadingView } from './ReadingView';

interface TopConsumersProps {
  /** Aggregated across the range on screen: average CPU, peak memory, total I/O. */
  processes: ProcessGroupRow[];
  /** The same shape, but as recorded at the newest reading inside that range. */
  latest: ProcessGroupRow[];
  /** When that reading was taken, or null when the range holds none. */
  latestTs: number | null;
  logicalProcessors: number;
  onSelectProcess: (name: string) => void;
  categoryFilter: ProcessCategory | 'all';
}

const MAX_ITEMS = 8;

type MetricView = 'cpu' | 'memory' | 'io';

interface MetricColorVars extends CSSProperties {
  '--metric-color': string;
}

function visible(rows: ProcessGroupRow[], categoryFilter: ProcessCategory | 'all'): ProcessGroupRow[] {
  const withoutIdle = rows.filter(p => p.name.toLowerCase() !== 'idle');
  if (categoryFilter === 'all') return withoutIdle;
  return withoutIdle.filter(p => categoriseProcess(p.name, p.path) === categoryFilter);
}

// What the numbers in each view actually are. Over time they are three different
// aggregates rather than one, and calling all three "usage" would let a total
// read as a rate, so each is named for what the viewer computed.
function metricLabel(metric: MetricView, view: ReadingView): string {
  if (view === 'now') {
    return metric === 'cpu' ? 'CPU, as a share of all cores'
      : metric === 'memory' ? 'memory in use'
      : 'I/O in that interval';
  }
  return metric === 'cpu' ? 'average CPU, as a share of all cores'
    : metric === 'memory' ? 'peak memory'
    : 'total I/O';
}

export function TopConsumers({
  processes, latest, latestTs, logicalProcessors, onSelectProcess, categoryFilter,
}: TopConsumersProps) {
  const [metric, setMetric] = useState<MetricView>('cpu');
  const [view, setView] = useState<ReadingView>('now');

  const rangeRows = useMemo(() => visible(processes, categoryFilter), [processes, categoryFilter]);
  const latestRows = useMemo(() => visible(latest, categoryFilter), [latest, categoryFilter]);
  const filtered = view === 'now' ? latestRows : rangeRows;

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

  // The panel goes away only when neither view has anything to show. Hiding it
  // because the view in force is empty would take the toggle with it, and the
  // user would have no way back to the view that does have rows.
  if (rangeRows.length === 0 && latestRows.length === 0) return null;

  // On a range that ends in the past, the newest reading in it is not now, and
  // saying "Now" without saying when would be a lie by a day. The timestamp is
  // what makes the tab's short label safe to use.
  const when = view === 'now'
    ? latestTs != null ? `at the ${formatTime(latestTs)} reading` : 'at the most recent reading'
    : 'across the range shown';

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
          <p className="top-consumers-subtitle">
            Processes ranked by {metricLabel(metric, view)}, {when}
          </p>
        </div>
        <div className="top-consumers-controls">
          <ReadingViewToggle value={view} onChange={setView} label="Top consumers reading" />
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
      </div>

      {items.length === 0 ? (
        <p className="consumer-empty" role="status">
          {view === 'now'
            ? 'Nothing was recorded at the most recent reading in this range.'
            : 'Nothing was recorded across this range.'}
        </p>
      ) : (
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
      )}
    </section>
  );
}
