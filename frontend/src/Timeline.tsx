import { useEffect, useRef, useState, useCallback } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { TimelinePoint, ProcessPoint } from './types';
import { DataTable, timelineColumns, processColumns } from './DataTable';

interface TimelineProps {
  data: TimelinePoint[];
  onRangeSelect?: (from: number, to: number) => void;
}

interface ProcessTimelineProps {
  data: ProcessPoint[];
  title: string;
}

const CHART_COLORS = {
  cpu: '#3b82f6',
  memory: '#10b981',
  disk: '#f59e0b',
  network: '#8b5cf6',
  io: '#ef4444',
};

function getThemeColors() {
  const isDark = document.documentElement.getAttribute('data-theme') === 'dark' ||
    (!document.documentElement.getAttribute('data-theme') &&
     window.matchMedia('(prefers-color-scheme: dark)').matches);

  return {
    axes: isDark ? '#9ca3af' : '#6b7280',
    grid: isDark ? '#374151' : '#e5e7eb',
    bg: isDark ? '#111827' : '#ffffff',
  };
}

export function Timeline({ data, onRangeSelect }: TimelineProps) {
  const [showTable, setShowTable] = useState(false);

  if (data.length === 0) {
    return <p className="no-data-msg">No timeline data for this range.</p>;
  }

  return (
    <div className="timeline-section">
      <div className="chart-controls">
        <button
          className="toggle-table-btn"
          onClick={() => setShowTable(!showTable)}
          aria-pressed={showTable}
        >
          {showTable ? 'Show Chart' : 'Show Table'}
        </button>
      </div>

      {showTable ? (
        <DataTable data={data} columns={timelineColumns()} caption="Machine timeline data" />
      ) : (
        <>
          <ChartPanel
            title="CPU %"
            data={data}
            seriesKey="cpuPct"
            color={CHART_COLORS.cpu}
            unit="%"
            onRangeSelect={onRangeSelect}
          />
          <ChartPanel
            title="Memory"
            data={data}
            seriesKey="memoryAvailMb"
            totalKey="memoryTotalMb"
            color={CHART_COLORS.memory}
            unit="MB"
            invert
          />
          <ChartPanel
            title="Disk Busy %"
            data={data}
            seriesKey="diskBusyPct"
            color={CHART_COLORS.disk}
            unit="%"
          />
          <ChartPanel
            title="Network KB/s"
            data={data}
            seriesKey="netKbps"
            color={CHART_COLORS.network}
            unit="KB/s"
          />
        </>
      )}
    </div>
  );
}

export function ProcessTimeline({ data, title }: ProcessTimelineProps) {
  const [showTable, setShowTable] = useState(false);

  if (data.length === 0) {
    return <p className="no-data-msg">No data for this process in the selected range.</p>;
  }

  return (
    <div className="timeline-section">
      <h3>{title}</h3>
      <div className="chart-controls">
        <button
          className="toggle-table-btn"
          onClick={() => setShowTable(!showTable)}
          aria-pressed={showTable}
        >
          {showTable ? 'Show Chart' : 'Show Table'}
        </button>
      </div>

      {showTable ? (
        <DataTable data={data} columns={processColumns()} caption={`${title} data`} />
      ) : (
        <>
          <ChartPanel title="CPU %" data={data} seriesKey="cpuPct" color={CHART_COLORS.cpu} unit="%" />
          <ChartPanel title="Memory MB" data={data} seriesKey="privateMb" color={CHART_COLORS.memory} unit="MB" />
          <ChartPanel title="I/O KB" data={data} seriesKey="ioKb" color={CHART_COLORS.io} unit="KB" />
        </>
      )}
    </div>
  );
}

function ChartPanel<T extends { ts: number }>({
  title, data, seriesKey, totalKey, color, unit, invert, onRangeSelect,
}: {
  title: string;
  data: T[];
  seriesKey: keyof T;
  totalKey?: keyof T;
  color: string;
  unit: string;
  invert?: boolean;
  onRangeSelect?: (from: number, to: number) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<uPlot | null>(null);

  const buildChart = useCallback(() => {
    if (!containerRef.current || data.length === 0) return;

    if (chartRef.current) {
      chartRef.current.destroy();
      chartRef.current = null;
    }

    const theme = getThemeColors();
    const timestamps = data.map(d => d.ts / 1000);

    const values = data.map(d => {
      const v = d[seriesKey];
      if (v === null || v === undefined) return null;
      const num = Number(v);
      if (invert && totalKey) {
        const total = d[totalKey];
        if (total !== null && total !== undefined) return Number(total) - num;
      }
      return num;
    });

    const series: uPlot.Series[] = [
      {},
      {
        label: title,
        stroke: color,
        fill: color + '20',
        width: 1.5,
        value: (_u: uPlot, v: number | null) => v !== null ? `${v.toFixed(1)} ${unit}` : '-',
        spanGaps: false,
      },
    ];

    const opts: uPlot.Options = {
      width: containerRef.current.clientWidth,
      height: 160,
      series,
      axes: [
        {
          stroke: theme.axes,
          grid: { stroke: theme.grid, width: 1 },
          ticks: { stroke: theme.grid, width: 1 },
        },
        {
          stroke: theme.axes,
          grid: { stroke: theme.grid, width: 1 },
          ticks: { stroke: theme.grid, width: 1 },
          size: 60,
          values: (_u: uPlot, vals: number[]) =>
            vals.map(v => v >= 1000 ? `${(v / 1024).toFixed(1)}k` : String(Math.round(v))),
        },
      ],
      cursor: {
        drag: { x: true, y: false },
      },
      hooks: onRangeSelect ? {
        setSelect: [(u: uPlot) => {
          const left = u.posToVal(u.select.left, 'x');
          const right = u.posToVal(u.select.left + u.select.width, 'x');
          if (right - left > 1) {
            onRangeSelect(left * 1000, right * 1000);
          }
          u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
        }],
      } : undefined,
      padding: [8, 8, 0, 0],
    };

    chartRef.current = new uPlot(opts, [timestamps, values], containerRef.current);
  }, [data, seriesKey, totalKey, color, unit, title, invert, onRangeSelect]);

  useEffect(() => {
    buildChart();
    return () => {
      chartRef.current?.destroy();
      chartRef.current = null;
    };
  }, [buildChart]);

  useEffect(() => {
    if (!containerRef.current) return;

    const observer = new ResizeObserver(entries => {
      for (const entry of entries) {
        chartRef.current?.setSize({
          width: entry.contentRect.width,
          height: 160,
        });
      }
    });
    observer.observe(containerRef.current);
    return () => observer.disconnect();
  }, []);

  return (
    <div className="chart-panel">
      <h4 className="chart-title">{title}</h4>
      <div ref={containerRef} className="chart-container" role="img" aria-label={`${title} chart`} />
    </div>
  );
}
