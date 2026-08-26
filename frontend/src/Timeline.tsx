import { useEffect, useRef, useState, useCallback } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { TimelinePoint, ProcessPoint, ThresholdConfig } from './types';
import { DataTable, timelineColumns, processColumns } from './DataTable';
import { formatSize, formatRate, computeMovingAverage, computeMean, computeLinearFit, CPU_OF_ONE_CORE, CPU_OF_ALL_CORES } from './utils';
import type { ChartThemeColors } from './chartTheme';
import { getThemeColors, pointsConfig, buildAxes } from './chartTheme';
import type { MetricKey } from './palette';
import { metricColor } from './palette';
import { useThemeMode } from './theme';

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

export interface OverlayConfig {
  movingAverage: boolean;
  mean: boolean;
  trend: boolean;
}

interface ThresholdLine {
  value: number;
  label: string;
}

function drawThresholdLines(u: uPlot, lines: ThresholdLine[], theme: ChartThemeColors) {
  const { ctx } = u;
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

function drawMeanLine(u: uPlot, mean: number, formatLabel: (v: number) => string, theme: ChartThemeColors) {
  const { ctx } = u;

  const y = u.valToPos(mean, 'y', true);
  if (y < u.bbox.top / devicePixelRatio || y > (u.bbox.top + u.bbox.height) / devicePixelRatio) return;

  ctx.save();
  ctx.setLineDash([8, 4]);
  ctx.strokeStyle = theme.meanLine;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.moveTo(u.bbox.left / devicePixelRatio, y);
  ctx.lineTo((u.bbox.left + u.bbox.width) / devicePixelRatio, y);
  ctx.stroke();

  ctx.setLineDash([]);
  ctx.font = '10px sans-serif';
  ctx.fillStyle = theme.meanText;
  ctx.textAlign = 'left';
  ctx.fillText(`avg ${formatLabel(mean)}`, u.bbox.left / devicePixelRatio + 4, y - 4);
  ctx.restore();
}

function drawTrendLine(u: uPlot, values: (number | null)[], timestamps: number[], theme: ChartThemeColors) {
  const fit = computeLinearFit(values);
  if (!fit) return;

  const { ctx } = u;

  const firstIdx = values.findIndex(v => v !== null);
  const lastIdx = values.length - 1 - [...values].reverse().findIndex(v => v !== null);
  if (firstIdx < 0 || lastIdx < 0 || firstIdx === lastIdx) return;

  const y0 = fit.intercept + fit.slope * firstIdx;
  const y1 = fit.intercept + fit.slope * lastIdx;

  const px0 = u.valToPos(timestamps[firstIdx], 'x', true);
  const py0 = u.valToPos(y0, 'y', true);
  const px1 = u.valToPos(timestamps[lastIdx], 'x', true);
  const py1 = u.valToPos(y1, 'y', true);

  ctx.save();
  ctx.setLineDash([4, 4]);
  ctx.strokeStyle = theme.trendLine;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.moveTo(px0, py0);
  ctx.lineTo(px1, py1);
  ctx.stroke();
  ctx.restore();
}

function OverlayToggle({ overlays, onChange }: { overlays: OverlayConfig; onChange: (o: OverlayConfig) => void }) {
  return (
    <div className="overlay-toggles" role="group" aria-label="Chart overlays">
      <button
        className={`overlay-btn ${overlays.movingAverage ? 'active' : ''}`}
        onClick={() => onChange({ ...overlays, movingAverage: !overlays.movingAverage })}
        aria-pressed={overlays.movingAverage}
        title="Moving average"
      >
        Avg
      </button>
      <button
        className={`overlay-btn ${overlays.mean ? 'active' : ''}`}
        onClick={() => onChange({ ...overlays, mean: !overlays.mean })}
        aria-pressed={overlays.mean}
        title="Mean for visible range"
      >
        Mean
      </button>
      <button
        className={`overlay-btn ${overlays.trend ? 'active' : ''}`}
        onClick={() => onChange({ ...overlays, trend: !overlays.trend })}
        aria-pressed={overlays.trend}
        title="Linear trend"
      >
        Trend
      </button>
    </div>
  );
}

export function Timeline({ data, onRangeSelect, thresholds }: TimelineProps) {
  const [showTable, setShowTable] = useState(false);
  const [overlays, setOverlays] = useState<OverlayConfig>({ movingAverage: false, mean: false, trend: false });

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
        {!showTable && <OverlayToggle overlays={overlays} onChange={setOverlays} />}
      </div>

      {showTable ? (
        <DataTable data={data} columns={timelineColumns()} caption="Machine timeline data" />
      ) : (
        <div className="charts-grid">
          <ChartPanel
            title={CPU_OF_ALL_CORES}
            data={data}
            seriesKey="cpuPct"
            metric="cpu"
            unit="%"
            onRangeSelect={handleRangeSelect}
            yMin={0}
            yMax={100}
            formatY={v => `${Math.round(v)}%`}
            overlays={overlays}
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
            metric="memory"
            unit="%"
            invert
            yMin={0}
            yMax={100}
            computePercent
            formatY={v => `${Math.round(v)}%`}
            overlays={overlays}
            thresholdLines={thresholds ? [
              { value: thresholds.system.memoryHighPct, label: `${thresholds.system.memoryHighPct}%` },
            ] : undefined}
          />
          <ChartPanel
            title="Disk Busy %"
            data={data}
            seriesKey="diskBusyPct"
            metric="disk"
            unit="%"
            onRangeSelect={handleRangeSelect}
            formatY={v => `${v.toFixed(1)}%`}
            overlays={overlays}
          />
          <ChartPanel
            title="Network"
            data={data}
            seriesKey="netKbps"
            metric="network"
            unit="KB/s"
            onRangeSelect={handleRangeSelect}
            formatY={v => {
              if (v >= 1048576) return `${(v / 1048576).toFixed(1)} GB/s`;
              if (v >= 1024) return `${(v / 1024).toFixed(1)} MB/s`;
              return `${Math.round(v)} KB/s`;
            }}
            formatTooltip={v => formatRate(v)}
            overlays={overlays}
          />
        </div>
      )}
    </div>
  );
}

export function ProcessTimeline({ data, title, thresholds }: ProcessTimelineProps) {
  const [showTable, setShowTable] = useState(false);
  const [overlays, setOverlays] = useState<OverlayConfig>({ movingAverage: false, mean: false, trend: false });

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
        {!showTable && <OverlayToggle overlays={overlays} onChange={setOverlays} />}
      </div>

      {showTable ? (
        <DataTable data={data} columns={processColumns()} caption={`${title} data`} />
      ) : (
        <div className="charts-grid">
          <ChartPanel
            title={CPU_OF_ONE_CORE}
            data={data}
            seriesKey="cpuPct"
            metric="cpu"
            unit="%"
            overlays={overlays}
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
            metric="memory"
            unit="MB"
            formatY={v => formatSize(v)}
            formatTooltip={v => formatSize(v)}
            overlays={overlays}
            thresholdLines={thresholds ? [
              { value: thresholds.process.memoryNotableMb, label: formatSize(thresholds.process.memoryNotableMb) },
              { value: thresholds.process.memoryHighMb, label: formatSize(thresholds.process.memoryHighMb) },
            ] : undefined}
          />
          <ChartPanel
            title="I/O KB"
            data={data}
            seriesKey="ioKb"
            metric="io"
            unit="KB"
            formatY={v => {
              if (v >= 1048576) return `${(v / 1048576).toFixed(1)} GB`;
              if (v >= 1024) return `${(v / 1024).toFixed(1)} MB`;
              return `${Math.round(v)} KB`;
            }}
            overlays={overlays}
          />
        </div>
      )}
    </div>
  );
}

function ChartPanel<T extends { ts: number }>({
  title, data, seriesKey, totalKey, metric, unit, invert, onRangeSelect,
  yMin, yMax, formatY, formatTooltip, computePercent, thresholdLines, overlays,
}: {
  title: string;
  data: T[];
  seriesKey: keyof T;
  totalKey?: keyof T;
  metric: MetricKey;
  unit: string;
  invert?: boolean;
  onRangeSelect?: (from: number, to: number) => void;
  yMin?: number;
  yMax?: number;
  formatY?: (v: number) => string;
  formatTooltip?: (v: number) => string;
  computePercent?: boolean;
  thresholdLines?: ThresholdLine[];
  overlays?: OverlayConfig;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<uPlot | null>(null);

  // A canvas cannot read var(--metric-cpu), so the series colour and the chart
  // furniture are resolved here, from the mode the hook reports. Both are
  // dependencies of the rebuild below, which is what makes a theme switch
  // repaint the chart instead of leaving it in the previous theme.
  const mode = useThemeMode();
  const color = metricColor(metric, mode);

  const buildChart = useCallback(() => {
    if (!containerRef.current || data.length === 0) return;

    if (chartRef.current) {
      chartRef.current.destroy();
      chartRef.current = null;
    }

    const theme = getThemeColors(mode);
    const timestamps = data.map(d => d.ts / 1000);

    const values: (number | null)[] = data.map(d => {
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
    const labelFormatter = formatY ?? ((v: number) => `${v.toFixed(1)} ${unit}`);

    const pointCount = data.length;

    const series: uPlot.Series[] = [
      {},
      {
        label: title,
        stroke: color,
        fill: color + '20',
        width: 1.5,
        value: (_u: uPlot, v: number | null) => v !== null ? tooltipFormatter(v) : '-',
        spanGaps: false,
        points: pointsConfig(pointCount, color),
      },
    ];

    const uPlotData: uPlot.AlignedData = [timestamps, values] as uPlot.AlignedData;

    if (overlays?.movingAverage) {
      const windowSize = Math.max(3, Math.round(pointCount / 15));
      const maValues = computeMovingAverage(values, windowSize);
      (uPlotData as (number | null)[][]).push(maValues);
      series.push({
        label: 'Moving avg',
        stroke: color + '80',
        width: 2,
        value: (_u: uPlot, v: number | null) => v !== null ? tooltipFormatter(v) : '-',
        spanGaps: true,
        points: { show: false },
      });
    }

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

    const drawHooks: ((u: uPlot) => void)[] = [];

    if (thresholdLines && thresholdLines.length > 0) {
      drawHooks.push((u: uPlot) => drawThresholdLines(u, thresholdLines, theme));
    }

    if (overlays?.mean) {
      const mean = computeMean(values);
      if (mean !== null) {
        drawHooks.push((u: uPlot) => drawMeanLine(u, mean, labelFormatter, theme));
      }
    }

    if (overlays?.trend) {
      drawHooks.push((u: uPlot) => drawTrendLine(u, values, timestamps, theme));
    }

    if (drawHooks.length > 0) {
      hooks.draw = drawHooks;
    }

    const opts: uPlot.Options = {
      width: containerRef.current.clientWidth,
      height: 320,
      series,
      scales: {
        y: {
          min: yMin,
          max: yMax,
        },
      },
      axes: buildAxes(theme, yAxisValues),
      cursor: {
        drag: { x: true, y: false },
      },
      hooks: Object.keys(hooks).length > 0 ? hooks : undefined,
      padding: [8, 8, 0, 0],
    };

    chartRef.current = new uPlot(opts, uPlotData, containerRef.current);
  }, [data, seriesKey, totalKey, color, mode, unit, title, invert, onRangeSelect, yMin, yMax, formatY, formatTooltip, computePercent, thresholdLines, overlays]);

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
          height: 320,
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
