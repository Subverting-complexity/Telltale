import { useEffect, useRef, useState, useCallback } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { getProcessGroup } from './api';
import type { ProcessPoint } from './types';

interface ProcessComparisonProps {
  names: string[];
  from: number;
  to: number;
  onBack: () => void;
}

const COMPARE_COLORS = ['#3b82f6', '#10b981', '#f59e0b', '#ef4444'];

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

interface CompareData {
  name: string;
  points: ProcessPoint[];
}

function CompareChart({ title, datasets, seriesKey, unit }: {
  title: string;
  datasets: CompareData[];
  seriesKey: keyof ProcessPoint;
  unit: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<uPlot | null>(null);

  const buildChart = useCallback(() => {
    if (!containerRef.current || datasets.length === 0) return;

    if (chartRef.current) {
      chartRef.current.destroy();
      chartRef.current = null;
    }

    // Build a unified timestamp array from all datasets
    const allTs = new Set<number>();
    for (const ds of datasets) {
      for (const p of ds.points) allTs.add(p.ts);
    }
    const timestamps = [...allTs].sort((a, b) => a - b);
    if (timestamps.length === 0) return;

    const tsSeconds = timestamps.map(t => t / 1000);

    // For each dataset, map values to the unified timestamp array
    const seriesData: (number | null)[][] = datasets.map(ds => {
      const lookup = new Map<number, number | null>();
      for (const p of ds.points) {
        const v = p[seriesKey];
        lookup.set(p.ts, v as number | null);
      }
      return timestamps.map(ts => lookup.get(ts) ?? null);
    });

    const theme = getThemeColors();

    const series: uPlot.Series[] = [
      {},
      ...datasets.map((ds, i) => ({
        label: ds.name,
        stroke: COMPARE_COLORS[i % COMPARE_COLORS.length],
        width: 2,
        value: (_u: uPlot, v: number | null) => v !== null ? `${v.toFixed(1)} ${unit}` : '-',
        spanGaps: false,
      })),
    ];

    const opts: uPlot.Options = {
      width: containerRef.current.clientWidth,
      height: 200,
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
        },
      ],
      cursor: { drag: { x: true, y: false } },
      padding: [8, 8, 0, 0],
    };

    chartRef.current = new uPlot(opts, [tsSeconds, ...seriesData], containerRef.current);
  }, [datasets, seriesKey, unit]);

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
        chartRef.current?.setSize({ width: entry.contentRect.width, height: 200 });
      }
    });
    observer.observe(containerRef.current);
    return () => observer.disconnect();
  }, []);

  return (
    <div className="chart-panel">
      <h4 className="chart-title">{title}</h4>
      <div ref={containerRef} className="chart-container" role="img" aria-label={`${title} comparison chart`} />
    </div>
  );
}

export function ProcessComparison({ names, from, to, onBack }: ProcessComparisonProps) {
  const [datasets, setDatasets] = useState<CompareData[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    Promise.all(
      names.map(name =>
        getProcessGroup(name, from, to)
          .then(res => ({ name: res.name, points: res.points }))
          .catch(() => ({ name, points: [] as ProcessPoint[] }))
      )
    ).then(results => {
      setDatasets(results);
      setLoading(false);
    });
  }, [names, from, to]);

  if (loading) return <p className="loading">Loading comparison data...</p>;

  return (
    <div className="process-detail">
      <button className="back-btn" onClick={onBack}>&larr; Back to process list</button>
      <h2>Process Comparison</h2>

      <div className="comparison-legend">
        {datasets.map((ds, i) => (
          <span key={ds.name} className="legend-item">
            <span className="legend-dot" style={{ backgroundColor: COMPARE_COLORS[i % COMPARE_COLORS.length] }} />
            {ds.name}
          </span>
        ))}
      </div>

      <CompareChart title="CPU %" datasets={datasets} seriesKey="cpuPct" unit="%" />
      <CompareChart title="Memory MB" datasets={datasets} seriesKey="privateMb" unit="MB" />
      <CompareChart title="I/O KB" datasets={datasets} seriesKey="ioKb" unit="KB" />
    </div>
  );
}
