import { useState } from 'react';
import type { ProcessGroupRow } from './types';
import { formatCpu, formatSize, formatIo } from './utils';

interface ProcessTableProps {
  processes: ProcessGroupRow[];
  onSelectGroup: (name: string) => void;
  filter: string;
  onFilterChange: (filter: string) => void;
  sortBy: string;
  onSortChange: (sort: string) => void;
}

type SortCol = 'cpu' | 'memory' | 'io' | 'name';

export function ProcessTable({
  processes, onSelectGroup, filter, onFilterChange, sortBy, onSortChange,
}: ProcessTableProps) {
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);

  const maxCpu = Math.max(...processes.map(p => p.cpuPct), 1);
  const maxMem = Math.max(...processes.map(p => p.privateMb), 1);

  function sortIcon(col: SortCol) {
    return sortBy === col ? ' ▼' : '';
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
      </div>

      <div className="process-table-wrapper" role="region" aria-label="Process list" tabIndex={0}>
        <table className="process-table">
          <caption className="sr-only">Processes ranked by resource usage</caption>
          <thead>
            <tr>
              <th scope="col" style={{ textAlign: 'left' }}>
                <button className="sort-btn" onClick={() => onSortChange('name')}>
                  Process{sortIcon('name')}
                </button>
              </th>
              <th scope="col">
                <button className="sort-btn" onClick={() => onSortChange('cpu')}>
                  CPU{sortIcon('cpu')}
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
            {processes.map(proc => (
              <tr
                key={proc.name}
                className="process-row"
                onClick={() => onSelectGroup(proc.name)}
                onKeyDown={e => { if (e.key === 'Enter') onSelectGroup(proc.name); }}
                tabIndex={0}
                role="button"
                aria-label={`View details for ${proc.name}`}
              >
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
                  {proc.name}
                </td>
                <td>
                  <div className="bar-cell">
                    <div
                      className="bar cpu-bar"
                      style={{ width: `${(proc.cpuPct / maxCpu) * 100}%` }}
                      role="meter"
                      aria-valuenow={proc.cpuPct}
                      aria-label={`CPU ${formatCpu(proc.cpuPct)}`}
                    />
                    <span className="bar-label">{formatCpu(proc.cpuPct)}</span>
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
            ))}
          </tbody>
        </table>
      </div>
      {processes.length === 0 && (
        <p className="no-data-msg">No processes found{filter ? ` matching "${filter}"` : ''}.</p>
      )}
    </div>
  );
}
