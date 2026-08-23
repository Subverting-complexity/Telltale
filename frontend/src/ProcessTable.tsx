import { useState } from 'react';
import type { ProcessGroupRow } from './types';
import { formatCpu, formatSize, formatIo, categoriseProcess } from './utils';
import type { ProcessCategory } from './utils';

interface ProcessTableProps {
  processes: ProcessGroupRow[];
  logicalProcessors: number;
  onSelectGroup: (name: string) => void;
  onCompare: (names: string[]) => void;
  filter: string;
  onFilterChange: (filter: string) => void;
  sortBy: string;
  onSortChange: (sort: string) => void;
  categoryFilter: ProcessCategory | 'all';
  onCategoryChange: (cat: ProcessCategory | 'all') => void;
}

type SortCol = 'cpu' | 'memory' | 'io' | 'name';

const CATEGORIES: { value: ProcessCategory | 'all'; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'applications', label: 'Applications' },
  { value: 'system', label: 'System' },
  { value: 'services', label: 'Services' },
];

const CATEGORY_COLORS: Record<ProcessCategory, string> = {
  system: 'var(--text-muted)',
  services: 'var(--accent)',
  applications: 'var(--success)',
};

export function ProcessTable({
  processes, logicalProcessors, onSelectGroup, onCompare,
  filter, onFilterChange, sortBy, onSortChange,
  categoryFilter, onCategoryChange,
}: ProcessTableProps) {
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);
  const [compareSet, setCompareSet] = useState<Set<string>>(new Set());

  const filtered = categoryFilter === 'all'
    ? processes
    : processes.filter(p => categoriseProcess(p.name, p.path) === categoryFilter);

  const maxCpu = Math.max(...filtered.map(p => p.cpuPct / logicalProcessors), 1);
  const maxMem = Math.max(...filtered.map(p => p.privateMb), 1);

  function sortIcon(col: SortCol) {
    return sortBy === col ? ' ▼' : '';
  }

  function toggleCompare(name: string) {
    setCompareSet(prev => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else if (next.size < 3) next.add(name);
      return next;
    });
  }

  function formatNormalisedCpu(rawPct: number): string {
    const normalised = rawPct / logicalProcessors;
    return formatCpu(normalised);
  }

  return (
    <div className="process-table-section">
      <div className="process-table-controls">
        <input
          type="search"
          className="process-filter"
          placeholder="Filter processes..."
          value={filter}
          onChange={e => onFilterChange(e.target.value)}
          aria-label="Filter processes by name"
        />

        <div className="category-filters" role="group" aria-label="Filter by category">
          {CATEGORIES.map(cat => (
            <button
              key={cat.value}
              className={`toggle-btn ${categoryFilter === cat.value ? 'active' : ''}`}
              onClick={() => onCategoryChange(cat.value)}
              aria-pressed={categoryFilter === cat.value}
            >
              {cat.label}
            </button>
          ))}
        </div>

        {compareSet.size >= 2 && (
          <button
            className="compare-btn"
            onClick={() => onCompare([...compareSet])}
          >
            Compare ({compareSet.size})
          </button>
        )}
      </div>

      <div className="process-table-wrapper" role="region" aria-label="Process list" tabIndex={0}>
        <table className="process-table">
          <caption className="sr-only">Processes ranked by resource usage</caption>
          <thead>
            <tr>
              <th scope="col" style={{ width: 32 }}>
                <span className="sr-only">Compare</span>
              </th>
              <th scope="col" style={{ textAlign: 'left' }}>
                <button className="sort-btn" onClick={() => onSortChange('name')}>
                  Process{sortIcon('name')}
                </button>
              </th>
              <th scope="col">
                <button className="sort-btn" onClick={() => onSortChange('cpu')}>
                  CPU %{sortIcon('cpu')}
                </button>
              </th>
              <th scope="col">
                <button className="sort-btn" onClick={() => onSortChange('memory')}>
                  Memory{sortIcon('memory')}
                </button>
              </th>
              <th scope="col">
                <button className="sort-btn" onClick={() => onSortChange('io')}>
                  I/O{sortIcon('io')}
                </button>
              </th>
              <th scope="col">#</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map(proc => {
              const category = categoriseProcess(proc.name, proc.path);
              const normCpu = proc.cpuPct / logicalProcessors;
              return (
                <tr
                  key={proc.name}
                  className="process-row"
                  onClick={() => onSelectGroup(proc.name)}
                  onKeyDown={e => { if (e.key === 'Enter') onSelectGroup(proc.name); }}
                  tabIndex={0}
                  role="button"
                  aria-label={`View details for ${proc.name}`}
                >
                  <td>
                    <input
                      type="checkbox"
                      className="compare-check"
                      checked={compareSet.has(proc.name)}
                      onChange={(e) => {
                        e.stopPropagation();
                        toggleCompare(proc.name);
                      }}
                      onClick={e => e.stopPropagation()}
                      aria-label={`Compare ${proc.name}`}
                      disabled={!compareSet.has(proc.name) && compareSet.size >= 3}
                    />
                  </td>
                  <td className="process-name" style={{ textAlign: 'left' }}>
                    <button
                      className="expand-btn"
                      onClick={e => {
                        e.stopPropagation();
                        setExpandedGroup(expandedGroup === proc.name ? null : proc.name);
                      }}
                      aria-expanded={expandedGroup === proc.name}
                      aria-label={`Expand ${proc.name}`}
                    >
                      {expandedGroup === proc.name ? '▼' : '▶'}
                    </button>
                    <span
                      className="category-dot"
                      style={{ backgroundColor: CATEGORY_COLORS[category] }}
                      title={category}
                    />
                    {proc.name}
                  </td>
                  <td>
                    <div className="bar-cell">
                      <div
                        className="bar cpu-bar"
                        style={{ width: `${(normCpu / maxCpu) * 100}%` }}
                        role="meter"
                        aria-valuenow={normCpu}
                        aria-label={`CPU ${formatNormalisedCpu(proc.cpuPct)}`}
                      />
                      <span className="bar-label">{formatNormalisedCpu(proc.cpuPct)}</span>
                    </div>
                  </td>
                  <td>
                    <div className="bar-cell">
                      <div
                        className="bar mem-bar"
                        style={{ width: `${(proc.privateMb / maxMem) * 100}%` }}
                        role="meter"
                        aria-valuenow={proc.privateMb}
                        aria-label={`Memory ${formatSize(proc.privateMb)}`}
                      />
                      <span className="bar-label">{formatSize(proc.privateMb)}</span>
                    </div>
                  </td>
                  <td>{formatIo(proc.ioKb)}</td>
                  <td>{proc.instanceCount}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {filtered.length === 0 && (
        <p className="no-data-msg">No processes found{filter ? ` matching "${filter}"` : ''}.</p>
      )}
    </div>
  );
}
