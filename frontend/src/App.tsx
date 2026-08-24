import { useEffect, useState, useCallback, useRef } from 'react';
import { getRange, getTimeline, getProcesses, getHealth, getThresholds } from './api';
import type {
  ViewState, ViewScale, Theme, RangeResponse,
  TimelinePoint, ProcessGroupRow, HealthResponse, ThresholdConfig,
  ProcessSelection,
} from './types';
import type { ProcessCategory } from './utils';
type DashboardTab = 'overview' | 'alerts' | 'processes';
import { StatusBar } from './StatusBar';
import { TimeNav } from './TimeNav';
import { Timeline } from './Timeline';
import { ProcessTable } from './ProcessTable';
import { ProcessDetail } from './ProcessDetail';
import { ProcessComparison } from './ProcessComparison';
import { Alerts } from './Alerts';
import { HealthSummary } from './HealthSummary';
import { TopConsumers } from './TopConsumers';
import { HeatmapView } from './Heatmap';
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
  const [selectedProcess, setSelectedProcess] = useState<ProcessSelection | null>(null);
  const [loading, setLoading] = useState(true);
  const [customRange, setCustomRange] = useState<{ from: number; to: number } | null>(null);
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [thresholds, setThresholds] = useState<ThresholdConfig | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<ProcessCategory | 'all'>('all');
  const [showHeatmap, setShowHeatmap] = useState(false);
  const [dashboardTab, setDashboardTab] = useState<DashboardTab>('overview');
  const [refreshKey, setRefreshKey] = useState(0);
  const [selectedHour, setSelectedHour] = useState<number | null>(null);

  const chartSectionRef = useRef<HTMLElement>(null);

  useEffect(() => { applyTheme(theme); }, [theme]);

  useEffect(() => {
    getRange().then(setRange).catch(() => {});
    getHealth().then(setHealth).catch(() => {});
    getThresholds().then(setThresholds).catch(() => {});
  }, []);

  const navigate = useCallback((newView: ViewState) => {
    setView(newView);
    updateUrl(newView);
    setSelectedProcess(null);
    setCustomRange(null);
    setSelectedHour(null);
  }, []);

  const refreshData = useCallback(() => {
    setRefreshKey(k => k + 1);
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
  }, [view, processSort, processFilter, customRange, refreshKey]);

  useEffect(() => {
    const id = setInterval(refreshData, 90_000);
    return () => clearInterval(id);
  }, [refreshData]);

  function handleRangeSelect(from: number, to: number) {
    setCustomRange({ from, to });
  }

  function handleHourSelect(from: number, to: number) {
    if (from === 0 && to === 0) {
      setCustomRange(null);
      setSelectedHour(null);
    } else {
      setCustomRange({ from, to });
      const d = new Date(from);
      setSelectedHour(d.getHours());
    }
  }

  function handleScrollTo(_metric: 'cpu' | 'memory' | 'disk' | 'network') {
    setDashboardTab('overview');
    setTimeout(() => chartSectionRef.current?.scrollIntoView({ behavior: 'smooth' }), 0);
  }

  function handleNavigateToDay(year: number, month: number, day: number) {
    navigate({ scale: 'day', year, month, day });
  }

  function cycleTheme() {
    const next: Theme = theme === 'system' ? 'light' : theme === 'light' ? 'dark' : 'system';
    setTheme(next);
  }

  const hasData = range?.min != null;
  const logicalProcessors = health?.logicalProcessors || 1;
  const showHeatmapToggle = view.scale === 'week' || view.scale === 'month' || view.scale === 'year';

  if (!hasData && !loading) {
    return (
      <div className="app">
        <header className="app-header" role="banner">
          <div className="header-brand">
            <span className="brand-mark" aria-hidden="true" />
            <h1>Telltale</h1>
          </div>
          <div className="header-actions">
            <button className="icon-btn" onClick={cycleTheme} aria-label={`Theme: ${theme}`} title={`Theme: ${theme}`}>
              {theme === 'dark' ? '●' : theme === 'light' ? '○' : '◐'}
            </button>
          </div>
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

  function renderDrillDown() {
    if (selectedProcess?.type === 'comparison') {
      return (
        <ProcessComparison
          names={selectedProcess.names}
          from={activeRange.from}
          to={activeRange.to}
          onBack={() => setSelectedProcess(null)}
        />
      );
    }

    if (selectedProcess?.type === 'instance') {
      return (
        <ProcessDetail
          type="instance"
          id={selectedProcess.id}
          groupName={selectedProcess.groupName}
          from={activeRange.from}
          to={activeRange.to}
          onBack={() => setSelectedProcess({ type: 'group', name: selectedProcess.groupName })}
          thresholds={thresholds}
        />
      );
    }

    if (selectedProcess?.type === 'group') {
      return (
        <ProcessDetail
          type="group"
          name={selectedProcess.name}
          from={activeRange.from}
          to={activeRange.to}
          onBack={() => setSelectedProcess(null)}
          onSelectInstance={(id, groupName) =>
            setSelectedProcess({ type: 'instance', id, groupName })
          }
          thresholds={thresholds}
        />
      );
    }

    return null;
  }

  function selectProcess(name: string) {
    setSelectedProcess({ type: 'group', name });
    setDashboardTab('processes');
  }

  const drillDown = renderDrillDown();

  return (
    <div className="app">
      <header className="app-header" role="banner">
        <div className="header-brand">
          <span className="brand-mark" aria-hidden="true" />
          <h1>Telltale</h1>
          <StatusBar />
        </div>
        <div className="header-actions">
          <button className="icon-btn" onClick={refreshData} aria-label="Refresh data" title="Refresh data">
            ↻
          </button>
          <button className="icon-btn" onClick={cycleTheme} aria-label={`Theme: ${theme}`} title={`Theme: ${theme}`}>
            {theme === 'dark' ? '●' : theme === 'light' ? '○' : '◐'}
          </button>
        </div>
      </header>

      <TimeNav
        view={view}
        onNavigate={navigate}
        onHourSelect={handleHourSelect}
        selectedHour={selectedHour}
        minTs={range?.min ?? null}
        maxTs={range?.max ?? null}
      />

      <main className="app-main" role="main">
        {loading && <div className="loading" aria-live="polite">Loading...</div>}

        <HealthSummary
          timeline={timeline}
          logicalProcessors={logicalProcessors}
          onScrollTo={handleScrollTo}
        />

        {customRange && (
          <div className="custom-range-bar">
            <span>
              {selectedHour !== null
                ? `Showing hour: ${selectedHour}:00 - ${selectedHour + 1}:00`
                : 'Custom range selected'}
            </span>
            <button onClick={() => { setCustomRange(null); setSelectedHour(null); }}>
              Show full {view.scale}
            </button>
          </div>
        )}

        {drillDown ? drillDown : (
          <>
            <nav className="dashboard-tabs" role="tablist" aria-label="Dashboard sections">
              {(['overview', 'alerts', 'processes'] as const).map(tab => (
                <button
                  key={tab}
                  role="tab"
                  aria-selected={dashboardTab === tab}
                  className={`dashboard-tab ${dashboardTab === tab ? 'active' : ''}`}
                  onClick={() => setDashboardTab(tab)}
                >
                  {tab === 'overview' ? 'Overview' : tab === 'alerts' ? 'Alerts' : 'Processes'}
                </button>
              ))}
            </nav>

            {dashboardTab === 'overview' && (
              <div className="tab-content">
                <TopConsumers
                  processes={processes}
                  logicalProcessors={logicalProcessors}
                  onSelectProcess={selectProcess}
                  categoryFilter={categoryFilter}
                />

                <section ref={chartSectionRef} className="section-card" aria-label="Machine timeline">
                  <div className="section-header">
                    <h2>System Overview</h2>
                    {showHeatmapToggle && (
                      <div className="view-toggle">
                        <button
                          className={`toggle-btn ${!showHeatmap ? 'active' : ''}`}
                          onClick={() => setShowHeatmap(false)}
                          aria-pressed={!showHeatmap}
                        >
                          Chart
                        </button>
                        <button
                          className={`toggle-btn ${showHeatmap ? 'active' : ''}`}
                          onClick={() => setShowHeatmap(true)}
                          aria-pressed={showHeatmap}
                        >
                          Heatmap
                        </button>
                      </div>
                    )}
                  </div>

                  {showHeatmap && showHeatmapToggle ? (
                    <HeatmapView
                      from={activeRange.from}
                      to={activeRange.to}
                      onNavigateToDay={handleNavigateToDay}
                    />
                  ) : (
                    <Timeline
                      data={timeline}
                      onRangeSelect={handleRangeSelect}
                      thresholds={thresholds}
                    />
                  )}
                </section>
              </div>
            )}

            {dashboardTab === 'alerts' && (
              <div className="tab-content">
                <Alerts
                  logicalProcessors={logicalProcessors}
                  onSelectProcess={selectProcess}
                />
              </div>
            )}

            {dashboardTab === 'processes' && (
              <div className="tab-content">
                <section className="section-card" aria-label="Process list">
                  <ProcessTable
                    processes={processes}
                    logicalProcessors={logicalProcessors}
                    onSelectGroup={name => setSelectedProcess({ type: 'group', name })}
                    onCompare={names => setSelectedProcess({ type: 'comparison', names })}
                    filter={processFilter}
                    onFilterChange={setProcessFilter}
                    sortBy={processSort}
                    onSortChange={setProcessSort}
                    categoryFilter={categoryFilter}
                    onCategoryChange={setCategoryFilter}
                  />
                  <p className="process-note">
                    CPU values are normalised to total system capacity. Some usage may be from processes shorter than the sampling interval.
                  </p>
                </section>
              </div>
            )}
          </>
        )}
      </main>
    </div>
  );
}
