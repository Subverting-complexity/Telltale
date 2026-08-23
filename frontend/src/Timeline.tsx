import { useEffect, useRef, useState, useCallback } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { TimelinePoint, ProcessPoint, ThresholdConfig } from './types';
import { DataTable, timelineColumns, processColumns } from './DataTable';
import { formatSize, formatRate } from './utils';

interface TimelineProps {
  data: TimelinePoint[];
  onRangeSelect?: (from: number, to: number) => void;
  thresholds?: ThresholdConfig | null;
}

interface ProcessTimelineProps {
  data: ProcessPoint[];
  title: string;
  thresholds?: ThresholdConfig | null;
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
    thresholdLine: isDark ? 'rgba(156,163,175,0.4)' : 'rgba(107,114,128,0.3)',
    thresholdText: isDark ? '#9ca3af' : '#9ca3af',
  };
}

interface ThresholdLine {
  value: number;
  label: string;
}

function drawThresholdLines(u: uPlot, lines: ThresholdLine[]) {
  const { ctx } = u;
  const theme = getThemeColors();
  const yAxis = u.axes[1];
  if (!yAxis) return;

  ctx.save();
  ctx.setLineDash([5, 5]);
  ctx.strokeStyle = theme.thresholdLine;
  ctx.lineWidth = 1;
  ctx.font = '10px sans-serif';
  ctx.fillStyle = theme.thresholdText;
  ctx.textAlign = 'right';

  for (const line of lines) {
    const y = u.valToPos(line.value, 'y', true);
    if (y < u.bbox.top / devicePixelRatio || y > (u.bbox.top + u.bbox.height) / devicePixelRatio) continue;

    ctx.beginPath();
    ctx.moveTo(u.bbox.left / devicePixelRatio, y);
    ctx.lineTo((u.bbox.left + u.bbox.width) / devicePixelRatio, y);
    ctx.stroke();

    ctx.fillText(line.label, (u.bbox.left + u.bbox.width) / devicePixelRatio - 2, y - 3);
  }

  ctx.restore();
}

export function Timeline({ data, onRangeSelect, thresholds }: TimelineProps) {
  const [showTable, setShowTable] = useState(false);

  if (data.length === 0) {
    return <p className="no-data-msg">No timeline data for this range.</p>;
  }

  const handleRangeSelect = onRangeSelect;

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
            onRangeSelect={handleRangeSelect}
            yMin={0}
            yMax={100}
            formatY={v => `${Math.round(v)}%`}
            thresholdLines={thresholds ? [
              { value: thresholds.system.cpuElevatedPct, label: `${thresholds.system.cpuElevatedPct}%` },
              { value: thresholds.system.cpuHighPct, label: `${thresholds.system.cpuHighPct}%` },
            ] : undefined}
          />
          <ChartPanel
            title="Memory"
            data={data}
            seriesKey="memoryAvailMb"
            totalKey="memoryTotalMb"
            color={CHART_COLORS.memory}
            unit="%"
            invert
            yMin={0}
            yMax={100}
            computePercent
            formatY={v => `${Math.round(v)}%`}
            thresholdLines={thresholds ? [
              { value: thresholds.system.memoryHighPct, label: `${thresholds.system.memoryHighPct}%` },
            ] : undefined}
          />
          <ChartPanel
            title="Disk Busy %"
            data={data}
            seriesKey="diskBusyPct"
            color={CHART_COLORS.disk}
            unit="%"
            onRangeSelect={handleRangeSelect}
            formatY={v => `${v.toFixed(1)}%`}
          />
          <ChartPanel
            title="Network"
            data={data}
            seriesKey="netKbps"
            color={CHART_COLORS.network}
            unit="KB/s"
            onRangeSelect={handleRangeSelect}
            formatY={v => {
              if (v >= 1048576) return `${(v / 1048576).toFixed(1)} GB/s`;
              if (v >= 1024) return `${(v / 1024).toFixed(1)} MB/s`;
              return `${Math.round(v)} KB/s`;
            }}
            formatTooltip={v => formatRate(v)}
          />
        </>
      )}
    </div>
  );
}

export function ProcessTimeline({ data, title, thresholds }: ProcessTimelineProps) {
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
          <ChartPanel
            title="CPU %"
            data={data}
            seriesKey="cpuPct"
            color={CHART_COLORS.cpu}
            unit="%"
            thresholdLines={thresholds ? [
              { value: thresholds.process.cpuNotablePct, label: `${thresholds.process.cpuNotablePct}%` },
              { value: thresholds.process.cpuElevatedPct, label: `${thresholds.process.cpuElevatedPct}%` },
              { value: thresholds.process.cpuHighPct, label: `${thresholds.process.cpuHighPct}%` },
            ] : undefined}
          />
          <ChartPanel
            title="Memory MB"
            data={data}
            seriesKey="privateMb"
            color={CHART_COLORS.memory}
            unit="MB"
            formatY={v => formatSize(v)}
            formatTooltip={v => formatSize(v)}
            thresholdLines={thresholds ? [
              { value: thresholds.process.memoryNotableMb, label: formatSize(thresholds.process.memoryNotableMb) },
              { value: thresholds.process.memoryHighMb, label: formatSize(thresholds.process.memoryHighMb) },
            ] : undefined}
          />
          <ChartPanel
            title="I/O KB"
            data={data}
            seriesKey="ioKb"
            color={CHART_COLORS.io}
            unit="KB"
            formatY={v => {
              if (v >= 1048576) return `${(v / 1048576).toFixed(1)} GB`;
              if (v >= 1024) return `${(v / 1024).toFixed(1)} MB`;
              return `${Math.round(v)} KB`;
            }}
          />
        </>
      )}
    </div>
  );
}

function ChartPanel<T extends { ts: number }>({
  title, data, seriesKey, totalKey, color, unit, invert, onRangeSelect,
  yMin, yMax, formatY, formatTooltip, computePercent, thresholdLines,
}: {
  title: string;
  data: T[];
  seriesKey: keyof T;
  totalKey?: keyof T;
  color: string;
  unit: string;
  invert?: boolean;
  onRangeSelect?: (from: number, to: number) => void;
  yMin?: number;
  yMax?: number;
  formatY?: (v: number) => string;
  formatTooltip?: (v: number) => string;
  computePercent?: boolean;
  thresholdLines?: ThresholdLine[];
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
        if (total !== null && total !== undefined) {
          const used = Number(total) - num;
          if (computePercent && Number(total) > 0) {
            return (used / Number(total)) * 100;
          }
          return used;
        }
      }
      return num;
    });

    const tooltipFormatter = formatTooltip ?? formatY ?? ((v: number) => `${v.toFixed(1)} ${unit}`);

    const series: uPlot.Series[] = [
      {},
      {
        label: title,
        stroke: color,
        fill: color + '20',
        width: 1.5,
        value: (_u: uPlot, v: number | null) => v !== null ? tooltipFormatter(v) : '-',
        spanGaps: false,
      },
    ];

    const yAxisValues = formatY
      ? (_u: uPlot, vals: number[]) => vals.map(formatY)
      : (_u: uPlot, vals: number[]) =>
          vals.map(v => v >= 1000 ? `${(v / 1024).toFixed(1)}k` : String(Math.round(v)));

    const hooks: uPlot.Hooks.Arrays = {};

    if (onRangeSelect) {
      hooks.setSelect = [(u: uPlot) => {
        const left = u.posToVal(u.select.left, 'x');
        const right = u.posToVal(u.select.left + u.select.width, 'x');
        if (right - left > 1) {
          onRangeSelect(left * 1000, right * 1000);
        }
        u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
      }];
    }

    if (thresholdLines && thresholdLines.length > 0) {
      hooks.draw = [(u: uPlot) => {
        drawThresholdLines(u, thresholdLines);
      }];
    }

    const opts: uPlot.Options = {
      width: containerRef.current.clientWidth,
      height: 160,
      series,
      scales: {
        y: {
          min: yMin,
          max: yMax,
        },
      },
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
          values: yAxisValues,
        },
      ],
      cursor: {
        drag: { x: true, y: false },
      },
      hooks: Object.keys(hooks).length > 0 ? hooks : undefined,
      padding: [8, 8, 0, 0],
    };

    chartRef.current = new uPlot(opts, [timestamps, values], containerRef.current);
  }, [data, seriesKey, totalKey, color, unit, title, invert, onRangeSelect, yMin, yMax, formatY, formatTooltip, computePercent, thresholdLines]);

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
