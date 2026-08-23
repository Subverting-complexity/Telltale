import { useEffect, useState, useCallback } from 'react';
import { getRange, getTimeline, getProcesses } from './api';
import type {
  ViewState, ViewScale, Theme, RangeResponse,
  TimelinePoint, ProcessGroupRow,
} from './types';
import { StatusBar } from './StatusBar';
import { TimeNav } from './TimeNav';
import { Timeline } from './Timeline';
import { ProcessTable } from './ProcessTable';
import { ProcessDetail } from './ProcessDetail';
import { Alerts } from './Alerts';
import {
  getDayRange, getMonthRange, getWeekRange, getYearRange,
} from './utils';

function getInitialTheme(): Theme {
  return (localStorage.getItem('telltale-theme') as Theme) ?? 'system';
}

function applyTheme(theme: Theme) {
  localStorage.setItem('telltale-theme', theme);
  if (theme === 'system') {
    document.documentElement.removeAttribute('data-theme');
  } else {
    document.documentElement.setAttribute('data-theme', theme);
  }
}

function getViewRange(view: ViewState): { from: number; to: number } {
  switch (view.scale) {
    case 'year':
      return getYearRange(view.year);
    case 'month':
      return getMonthRange(view.year, view.month ?? 1);
    case 'week':
      return getWeekRange(view.year, view.month ?? 1, view.day ?? 1);
    case 'day':
      return getDayRange(view.year, view.month ?? 1, view.day ?? 1);
  }
}

function parseUrlParams(): ViewState | null {
  const params = new URLSearchParams(window.location.search);
  const year = params.get('year');
  if (!year) return null;
  const state: ViewState = { scale: 'day', year: parseInt(year) };
  const month = params.get('month');
  if (month) state.month = parseInt(month);
  const day = params.get('day');
  if (day) { state.day = parseInt(day); state.scale = 'day'; }
  else if (month) state.scale = 'month';
  else state.scale = 'year';
  const scale = params.get('scale') as ViewScale;
  if (scale) state.scale = scale;
  return state;
}

function updateUrl(view: ViewState) {
  const params = new URLSearchParams();
  params.set('year', String(view.year));
  if (view.month) params.set('month', String(view.month));
  if (view.day) params.set('day', String(view.day));
  params.set('scale', view.scale);
  window.history.replaceState(null, '', `?${params}`);
}

export default function App() {
  const now = new Date();
  const [theme, setTheme] = useState<Theme>(getInitialTheme);
  const [range, setRange] = useState<RangeResponse | null>(null);
  const [view, setView] = useState<ViewState>(() => {
    return parseUrlParams() ?? {
      scale: 'day',
      year: now.getFullYear(),
      month: now.getMonth() + 1,
      day: now.getDate(),
    };
  });

  const [timeline, setTimeline] = useState<TimelinePoint[]>([]);
  const [processes, setProcesses] = useState<ProcessGroupRow[]>([]);
  const [processFilter, setProcessFilter] = useState('');
  const [processSort, setProcessSort] = useState('cpu');
  const [selectedProcess, setSelectedProcess] = useState<{ type: 'group'; name: string } | null>(null);
  const [loading, setLoading] = useState(true);
  const [customRange, setCustomRange] = useState<{ from: number; to: number } | null>(null);

  useEffect(() => { applyTheme(theme); }, [theme]);

  useEffect(() => {
    getRange().then(setRange).catch(() => {});
  }, []);

  const navigate = useCallback((newView: ViewState) => {
    setView(newView);
    updateUrl(newView);
    setSelectedProcess(null);
    setCustomRange(null);
  }, []);

  useEffect(() => {
    setLoading(true);
    const { from, to } = customRange ?? getViewRange(view);

    Promise.all([
      getTimeline(from, to).catch(() => ({ resolution: '', points: [] })),
      getProcesses(from, to, {
        limit: 50,
        sort: processSort,
        q: processFilter || undefined,
      }).catch(() => ({ grouped: true, processes: [] })),
    ]).then(([tl, procs]) => {
      setTimeline(tl.points);
      setProcesses(procs.processes as ProcessGroupRow[]);
      setLoading(false);
    });
  }, [view, processSort, processFilter, customRange]);

  function handleRangeSelect(from: number, to: number) {
    setCustomRange({ from, to });
  }

  function cycleTheme() {
    const next: Theme = theme === 'system' ? 'light' : theme === 'light' ? 'dark' : 'system';
    setTheme(next);
  }

  const hasData = range?.min != null;

  if (!hasData && !loading) {
    return (
      <div className="app">
        <header className="app-header" role="banner">
          <h1>Telltale</h1>
          <button className="theme-btn" onClick={cycleTheme} aria-label={`Theme: ${theme}`}>
            {theme === 'dark' ? '●' : theme === 'light' ? '○' : '◐'}
          </button>
        </header>
        <main className="app-main no-data" role="main">
          <h2>No data yet</h2>
          <p>Start the Telltale collector to begin recording process activity.</p>
          <p>Run <code>Collector.exe</code> or add it to Task Scheduler to start automatically.</p>
        </main>
      </div>
    );
  }

  const activeRange = customRange ?? getViewRange(view);

  return (
    <div className="app">
      <header className="app-header" role="banner">
        <h1>Telltale</h1>
        <StatusBar />
        <button className="theme-btn" onClick={cycleTheme} aria-label={`Theme: ${theme}`}>
          {theme === 'dark' ? '●' : theme === 'light' ? '○' : '◐'}
        </button>
      </header>

      <TimeNav
        view={view}
        onNavigate={navigate}
        minTs={range?.min ?? null}
        maxTs={range?.max ?? null}
      />

      <main className="app-main" role="main">
        {loading && <div className="loading" aria-live="polite">Loading...</div>}

        {selectedProcess ? (
          <ProcessDetail
            type={selectedProcess.type}
            name={selectedProcess.name}
            from={activeRange.from}
            to={activeRange.to}
            onBack={() => setSelectedProcess(null)}
          />
        ) : (
          <>
            {customRange && (
              <div className="custom-range-bar">
                <span>Custom range selected</span>
                <button onClick={() => setCustomRange(null)}>Clear selection</button>
              </div>
            )}

            <Alerts onSelectProcess={name => setSelectedProcess({ type: 'group', name })} />

            <section aria-label="Machine timeline">
              <h2>System Overview</h2>
              <Timeline data={timeline} onRangeSelect={handleRangeSelect} />
            </section>

            <section aria-label="Process list">
              <h2>Processes</h2>
              <ProcessTable
                processes={processes}
                onSelectGroup={name => setSelectedProcess({ type: 'group', name })}
                filter={processFilter}
                onFilterChange={setProcessFilter}
                sortBy={processSort}
                onSortChange={setProcessSort}
              />
              <p className="process-note">
                Some CPU usage may be from processes shorter than the sampling interval.
              </p>
            </section>
          </>
        )}
      </main>
    </div>
  );
}
