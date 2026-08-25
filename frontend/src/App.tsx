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
import { TimeNav, pad2 } from './TimeNav';
import type { HourSelection } from './TimeNav';
import { Timeline } from './Timeline';
import { ProcessTable } from './ProcessTable';
import { ProcessDetail } from './ProcessDetail';
import { ProcessComparison } from './ProcessComparison';
import { Alerts } from './Alerts';
import { HealthSummary } from './HealthSummary';
import { TopConsumers } from './TopConsumers';
import { HeatmapView } from './Heatmap';
import { WipeDataDialog } from './WipeDataDialog';
import {
  getDayRange, getMonthRange, getWeekRange, getYearRange, viewedDay,
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
  // Changing the date range invalidates any drill-down history entry
  // pushed for the previous range, so the history state is reset to
  // `null` alongside the URL — otherwise Forward could resurface a
  // process detail view for data that's no longer the active range.
  window.history.replaceState({ selectedProcess: null }, '', `?${params}`);
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
  const [wipeOpen, setWipeOpen] = useState(false);
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [thresholds, setThresholds] = useState<ThresholdConfig | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<ProcessCategory | 'all'>('all');
  const [showHeatmap, setShowHeatmap] = useState(false);
  const [dashboardTab, setDashboardTab] = useState<DashboardTab>('overview');
  const [refreshKey, setRefreshKey] = useState(0);
  const [selectedHourRange, setSelectedHourRange] = useState<HourSelection | null>(null);

  const chartSectionRef = useRef<HTMLElement>(null);

  useEffect(() => { applyTheme(theme); }, [theme]);

  useEffect(() => {
    function handleKeyDown(e: KeyboardEvent) {
      // The page's own shortcuts stand down while the wipe dialog is open. The
      // dialog is a modal on top of the page, but these listen on the window, so
      // an arrow key pressed with focus on the Cancel button would step the view
      // to another day underneath it, and the day the dialog is offering to
      // delete would move with it. Escape had the same shape of problem: one
      // press would close the dialog and pop the drill-down behind it.
      if (wipeOpen) return;

      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;

      if (e.key === 'Escape' && selectedProcess) {
        e.preventDefault();
        window.history.back();
        return;
      }

      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        if (selectedProcess) {
          if (e.key === 'ArrowLeft') {
            e.preventDefault();
            window.history.back();
          }
          return;
        }
        e.preventDefault();
        const step = e.key === 'ArrowLeft' ? -1 : 1;
        setView(v => {
          let newView: ViewState;
          switch (v.scale) {
            case 'year':
              newView = { ...v, year: v.year + step };
              break;
            case 'month': {
              let m = (v.month ?? 1) + step;
              let y = v.year;
              if (m < 1) { m = 12; y--; }
              else if (m > 12) { m = 1; y++; }
              newView = { ...v, year: y, month: m };
              break;
            }
            case 'week':
            case 'day': {
              const d = new Date(v.year, (v.month ?? 1) - 1, (v.day ?? 1));
              d.setDate(d.getDate() + step * (v.scale === 'week' ? 7 : 1));
              newView = { ...v, year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate() };
              break;
            }
          }
          updateUrl(newView);
          return newView;
        });
        setCustomRange(null);
        setSelectedHourRange(null);
      }
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedProcess, wipeOpen]);

  // Refreshed rather than read once. Deleting the earliest or the latest day
  // moves where the recording starts and ends, and the navigation is built from
  // exactly those two numbers, so a stale pair leaves the user able to walk into
  // days that no longer exist.
  useEffect(() => {
    getRange().then(setRange).catch(() => {});
    getHealth().then(setHealth).catch(() => {});
    getThresholds().then(setThresholds).catch(() => {});
  }, [refreshKey]);

  const navigate = useCallback((newView: ViewState) => {
    setView(newView);
    updateUrl(newView);
    setSelectedProcess(null);
    setCustomRange(null);
    setSelectedHourRange(null);
  }, []);

  // Drilling into a process (group, instance, or comparison) pushes a
  // history entry so the browser's own Back/Forward controls, and the
  // Escape/ArrowLeft handlers below, can step through the drill-down via
  // `window.history.back()` instead of each juggling the previous state
  // by hand.
  const pushSelection = useCallback((selection: ProcessSelection | null) => {
    window.history.pushState({ selectedProcess: selection }, '');
    setSelectedProcess(selection);
  }, []);

  useEffect(() => {
    function handlePopState(e: PopStateEvent) {
      const selection = (e.state?.selectedProcess as ProcessSelection | undefined) ?? null;
      setSelectedProcess(selection);
    }
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
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

  function handleHourSelect(selection: HourSelection | null) {
    if (selection === null) {
      setCustomRange(null);
      setSelectedHourRange(null);
    } else {
      setCustomRange({ from: selection.from, to: selection.to });
      setSelectedHourRange(selection);
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

  // Named rather than written inline, so the return below reads as one line and
  // the reason the empty screen waits for it stays next to the condition.
  const wipeDialog = wipeOpen ? (
    <WipeDataDialog
      day={viewedDay(view)}
      onClose={() => setWipeOpen(false)}
      onWiped={() => {
        // The deleted range has to read as empty straight away, and the range
        // endpoint has to be asked again: wiping the earliest day moves where
        // the recording starts.
        setCustomRange(null);
        setSelectedHourRange(null);
        setSelectedProcess(null);
        // Wiping is reachable while a drill-down is open (the header button
        // isn't gated on it), and that drill-down's history entry would
        // otherwise survive the wipe. Without this, Back then Forward could
        // still land on a process whose data was just deleted.
        window.history.replaceState({ selectedProcess: null }, '');
        refreshData();
      }}
    />
  ) : null;
  const showHeatmapToggle = view.scale === 'week' || view.scale === 'month' || view.scale === 'year';

  // The empty screen waits while the dialog is open. Wiping everything empties
  // the recording, which flips hasData false, and this return replaces the whole
  // page: React sees a different tree in the same place, tears the dialog down
  // and builds a new one with none of its state. The user would be told what had
  // just been deleted for exactly as long as it took the range endpoint to
  // answer. Nothing is hidden by waiting, because the dialog is on top of the
  // page anyway, and the screen appears as soon as it is closed.
  if (!hasData && !loading && !wipeOpen) {
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
          <p>Run <code>TelltaleCapture.exe</code> or add it to Task Scheduler to start automatically.</p>
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
          onBack={() => window.history.back()}
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
          onBack={() => window.history.back()}
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
          onBack={() => window.history.back()}
          onSelectInstance={(id, groupName) =>
            pushSelection({ type: 'instance', id, groupName })
          }
          thresholds={thresholds}
        />
      );
    }

    return null;
  }

  function selectProcess(name: string) {
    pushSelection({ type: 'group', name });
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
          <button
            className="icon-btn"
            onClick={() => setWipeOpen(true)}
            aria-label="Delete recorded data"
            title="Delete recorded data"
          >
            🗑
          </button>
        </div>
      </header>

      <TimeNav
        view={view}
        onNavigate={navigate}
        onHourSelect={handleHourSelect}
        selectedHourRange={selectedHourRange}
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
              {selectedHourRange
                ? selectedHourRange.startHour === selectedHourRange.endHour
                  ? `Showing hour: ${pad2(selectedHourRange.startHour)}:00 - ${pad2(selectedHourRange.startHour + 1)}:00`
                  : `Showing hours: ${pad2(selectedHourRange.startHour)}:00 - ${pad2(selectedHourRange.endHour + 1)}:00`
                : 'Custom range selected'}
            </span>
            <button onClick={() => { setCustomRange(null); setSelectedHourRange(null); }}>
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
                    onSelectGroup={name => pushSelection({ type: 'group', name })}
                    onCompare={names => pushSelection({ type: 'comparison', names })}
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

      {wipeDialog}
    </div>
  );
}
