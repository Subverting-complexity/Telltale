import type { TimelinePoint, ProcessPoint } from './types';
import { formatCpu, formatSize, formatIo, formatTime } from './utils';

interface DataTableProps<T> {
  data: T[];
  columns: ColumnDef<T>[];
  caption: string;
}

interface ColumnDef<T> {
  key: string;
  label: string;
  render: (row: T) => string;
  align?: 'left' | 'right';
}

export function DataTable<T>({ data, columns, caption }: DataTableProps<T>) {
  if (data.length === 0) return <p>No data available.</p>;

  const pageSize = 50;
  const displayData = data.slice(0, pageSize);

  return (
    <div className="data-table-wrapper" role="region" aria-label={caption} tabIndex={0}>
      <table className="data-table">
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map(col => (
              <th key={col.key} scope="col" style={{ textAlign: col.align ?? 'right' }}>
                {col.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {displayData.map((row, i) => (
            <tr key={i}>
              {columns.map(col => (
                <td key={col.key} style={{ textAlign: col.align ?? 'right' }}>
                  {col.render(row)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {data.length > pageSize && (
        <p className="data-table-note">Showing {pageSize} of {data.length} rows.</p>
      )}
    </div>
  );
}

export function timelineColumns(): ColumnDef<TimelinePoint>[] {
  return [
    { key: 'time', label: 'Time', render: r => formatTime(r.ts), align: 'left' },
    { key: 'cpu', label: 'CPU %', render: r => formatCpu(r.cpuPct) },
    { key: 'memAvail', label: 'Mem Avail', render: r => r.memoryAvailMb != null ? formatSize(r.memoryAvailMb) : '-' },
    { key: 'memTotal', label: 'Mem Total', render: r => r.memoryTotalMb != null ? formatSize(r.memoryTotalMb) : '-' },
    { key: 'disk', label: 'Disk %', render: r => r.diskBusyPct != null ? `${r.diskBusyPct.toFixed(1)}%` : '-' },
    { key: 'net', label: 'Net KB/s', render: r => r.netKbps != null ? `${r.netKbps.toFixed(0)}` : '-' },
  ];
}

export function processColumns(): ColumnDef<ProcessPoint>[] {
  return [
    { key: 'time', label: 'Time', render: r => formatTime(r.ts), align: 'left' },
    { key: 'cpu', label: 'CPU %', render: r => formatCpu(r.cpuPct) },
    { key: 'mem', label: 'Private MB', render: r => r.privateMb != null ? formatSize(r.privateMb) : '-' },
    { key: 'ws', label: 'Working Set', render: r => r.workingSetMb != null ? formatSize(r.workingSetMb) : '-' },
    { key: 'io', label: 'I/O', render: r => formatIo(r.ioKb) },
  ];
}
