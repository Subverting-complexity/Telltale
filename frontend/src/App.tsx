import { useEffect, useState, useCallback, useRef } from 'react';
import { getRange, getTimeline, getProcesses, getHealth, getThresholds, isAbort } from './api';
import type {
  ViewState, Theme, RangeResponse,
  TimelinePoint, TimelineResponse, ProcessGroupRow, HealthResponse, ThresholdConfig,
  ProcessSelection, DashboardTab, ProcessSort,
} from './types';
import { TimelineCache } from './timelineCache';
import type { ProcessCategory } from './utils';
import {
  loadViewPreferences, saveViewPreferences, restoredGranularity, viewForScale, isViewScale,
} from './viewPreferences';
import type { ViewPreferences } from './viewPreferences';
import { StatusBar } from './StatusBar';
import { TimeNav, pad2 } from './TimeNav';
import type { HourSelection } from './TimeNav';
import { GRANULARITIES, granularityById, clampNotice } from './granularity';
import type { GranularityId, TimelineDetail } from './granularity';
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

function drillDownLabel(selection: ProcessSelection): string {
  switch (selection.type) {
    case 'group':
      return selection.name;
    case 'instance':
      return selection.groupName;
    case 'comparison':
      return selection.names.join(', ');
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
    default:
      return getDayRange(view.year, view.month ?? 1, view.day ?? 1);
  }
}

function parseUrlParams(): ViewState | null {
  const params = new URLSearchParams(window.location.search);
  const year = params.get('year');
  if (!year) return null;
  const parsedYear = parseInt(year);
  if (isNaN(parsedYear)) return null;
  const state: ViewState = { scale: 'day', year: parsedYear };
  const month = params.get('month');
  if (month) {
    const m = parseInt(month);
    if (!isNaN(m)) state.month = m;
  }
  const day = params.get('day');
  if (day) {
    const d = parseInt(day);
    if (!isNaN(d)) { state.day = d; state.scale = 'day'; }
    else if (state.month) state.scale = 'month';
    else state.scale = 'year';
  } else if (state.month) state.scale = 'month';
  else state.scale = 'year';
  const scale = params.get('scale');
  if (isViewScale(scale)) state.scale = scale;
  return state;
}

/**
 * The granularity the URL asks for, or null when it does not ask for one.
 *
 * The two are worth telling apart. A URL that says nothing leaves the saved
 * preference free to apply; one that names a width nobody recognises has still
 * spoken, and Auto is the answer to it.
 */
function parseUrlGranularity(): GranularityId | null {
  const g = new URLSearchParams(window.location.search).get('g');
  if (g === null) return null;
  return GRANULARITIES.some(o => o.id === g) ? g as GranularityId : 'auto';
}

/**
 * What the window opens on.
 *
 * The URL wins outright where it carries a view: it is either a link someone was
 * given or a history entry being restored, and in both cases it describes a
 * particular thing to look at rather than a habit. The saved preferences apply
 * only to a bare URL, which is what Telltale itself opens the window on.
 */
function initialSettings(): {
  view: ViewState;
  granularity: GranularityId;
  preferences: ViewPreferences;
} {
  const preferences = loadViewPreferences();
  const urlView = parseUrlParams();
  const urlGranularity = parseUrlGranularity();

  if (urlView) {
    return { view: urlView, granularity: urlGranularity ?? 'auto', preferences };
  }

  return {
    view: viewForScale(preferences.scale, new Date()),
    granularity: urlGranularity ?? restoredGranularity(preferences),
    preferences,
  };
}

function buildUrlParams(view: ViewState, granularity: GranularityId): URLSearchParams {
  const params = new URLSearchParams();
  params.set('year', String(view.year));
  if (view.month) params.set('month', String(view.month));
  if (view.day) params.set('day', String(view.day));
  params.set('scale', view.scale);
  // Auto is the default, so it is left out rather than written. A shared link
  // then carries a granularity only when one was actually chosen.
  if (granularity !== 'auto') params.set('g', granularity);
  const token = new URLSearchParams(window.location.search).get('s');
  if (token) params.set('s', token);
  return params;
}

function updateUrl(view: ViewState, granularity: GranularityId) {
  // Changing the date range invalidates any drill-down history entry
  // pushed for the previous range, so the history state is reset to
  // `null` alongside the URL — otherwise Forward could resurface a
  // process detail view for data that's no longer the active range.
  window.history.replaceState({ selectedProcess: null }, '', `?${buildUrlParams(view, granularity)}`);
}

/**
 * Records a granularity change in the URL without touching the history state.
 *
 * Changing how finely the chart is divided does not change which range is on
 * screen, so any drill-down entry pushed for that range is still valid and must
 * survive, unlike a navigation, which invalidates it.
 */
function updateGranularityUrl(view: ViewState, granularity: GranularityId) {
  window.history.replaceState(window.history.state, '', `?${buildUrlParams(view, granularity)}`);
}

/**
 * What the screen falls back to when a timeline request genuinely fails.
 *
 * The floors are zero, which the picker reads as "no floor known", so every
 * option stays offered rather than being greyed out on the strength of a
 * request that never arrived.
 */
const EMPTY_TIMELINE: TimelineResponse = {
  resolution: '', bucketMs: 0, bucketRequestMs: null, minBucketMs: 0, tierFloorMs: 0, points: [],
};

export default function App() {
  // Read once, when the window opens. From here on the state below is the truth
  // and the effect further down writes it back; nothing reads storage again.
  const [initial] = useState(initialSettings);

  const [theme, setTheme] = useState<Theme>(getInitialTheme);
  const [range, setRange] = useState<RangeResponse | null>(null);
  /** Whether the range request has been answered, successfully or not. */
  const [rangeLoaded, setRangeLoaded] = useState(false);
  const [view, setView] = useState<ViewState>(initial.view);

  const [timeline, setTimeline] = useState<TimelinePoint[]>([]);
  const [granularity, setGranularity] = useState<GranularityId>(initial.granularity);
  // What the last response was actually served at, which is what the picker
  // needs to know which options this window can offer and what the chart needs
  // to say when a request was widened.
  const [timelineDetail, setTimelineDetail] = useState<TimelineDetail | null>(null);
  const [processes, setProcesses] = useState<ProcessGroupRow[]>([]);
  // Not restored, and deliberately so. It is the one piece of view state that
  // records what someone went looking for, and a filter in force on open reads
  // as missing data rather than as a filter.
  const [processFilter, setProcessFilter] = useState('');
  const [processSort, setProcessSort] = useState<ProcessSort>(initial.preferences.sort);
  const [selectedProcess, setSelectedProcess] = useState<ProcessSelection | null>(null);
  const [loading, setLoading] = useState(true);
  // The chart's own wait, separate from the dashboard's. A granularity change
  // cannot alter the process table, the health summary or the heatmap, so
  // blanking the page for one said the whole screen was out of date when only
  // the chart was. This is also false for an answer served from memory, which
  // arrives in the same tick and has nothing to wait for.
  const [timelineLoading, setTimelineLoading] = useState(true);
  const [customRange, setCustomRange] = useState<{ from: number; to: number } | null>(null);
  const [wipeOpen, setWipeOpen] = useState(false);
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [thresholds, setThresholds] = useState<ThresholdConfig | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<ProcessCategory | 'all'>(
    initial.preferences.category,
  );
  // Restored, but only offered above Day scale. A saved heatmap on a day view
  // draws the chart until the scale widens, which is what the toggle itself does.
  const [showHeatmap, setShowHeatmap] = useState(initial.preferences.heatmap);
  const [dashboardTab, setDashboardTab] = useState<DashboardTab>(initial.preferences.tab);
  const [refreshKey, setRefreshKey] = useState(0);
  const [selectedHourRange, setSelectedHourRange] = useState<HourSelection | null>(null);

  const chartSectionRef = useRef<HTMLElement>(null);

  /** Sequence number of the most recent timeline request, so stale ones can be dropped. */
  const latestRequest = useRef(0);

  /**
   * The same guard for the process list, which now runs on its own schedule.
   * Changing the sort and changing it back quickly can leave two in flight, and
   * the first is not guaranteed to answer first.
   */
  const latestProcessRequest = useRef(0);

  /**
   * Puts a timeline answer on screen, whether it came from the network or from
   * the cache. Both paths have to set the floors as well as the series: the
   * picker reads them to decide what to offer, and the clamp notice reads them
   * to explain a width the server widened.
   */
  const applyTimeline = useCallback((tl: TimelineResponse) => {
    setTimeline(tl.points);
    setTimelineDetail({
      bucketMs: tl.bucketMs,
      bucketRequestMs: tl.bucketRequestMs,
      minBucketMs: tl.minBucketMs,
      tierFloorMs: tl.tierFloorMs,
    });
  }, []);

  useEffect(() => { applyTheme(theme); }, [theme]);

  // Written on every change rather than on the way out, because a window can go
  // away without warning and there is no moment this page is guaranteed to get.
  // It runs on mount too, writing back what was just read, which is how the
  // entry comes to exist after a first open that changed nothing.
  //
  // The scale is stored twice over: once as the scale to restore, and once as
  // the scale the granularity belongs to. They are the same here because
  // `navigate` returns the granularity to Auto whenever the scale changes, so
  // the width in force always belongs to the scale in force.
  useEffect(() => {
    saveViewPreferences({
      scale: view.scale,
      granularity,
      granularityScale: view.scale,
      tab: dashboardTab,
      heatmap: showHeatmap,
      sort: processSort,
      category: categoryFilter,
    });
  }, [view.scale, granularity, dashboardTab, showHeatmap, processSort, categoryFilter]);

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
          updateUrl(newView, granularity);
          return newView;
        });
        setCustomRange(null);
        setSelectedHourRange(null);
      }
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
    // The arrow keys rewrite the URL, which has to carry the granularity in
    // force, so the handler is re-bound when that changes.
  }, [selectedProcess, wipeOpen, granularity]);

  // Refreshed rather than read once. Deleting the earliest or the latest day
  // moves where the recording starts and ends, and the navigation is built from
  // exactly those two numbers, so a stale pair leaves the user able to walk into
  // days that no longer exist.
  useEffect(() => {
    // `rangeLoaded` only ever goes true, including when the request failed. The
    // "No data yet" screen is a statement about the recording, so it waits for
    // this rather than for the dashboard's loading flag, which used to stand in
    // for it: that flag now clears when the process list lands, and the process
    // list landing says nothing about whether the recording has a start and an
    // end yet. A failed range request still has to reach an answer, otherwise
    // the screen would wait forever on a question that is not going to be
    // answered.
    getRange().then(setRange).catch(() => {}).finally(() => setRangeLoaded(true));
    getHealth().then(setHealth).catch(() => {});
    getThresholds().then(setThresholds).catch(() => {});
  }, [refreshKey]);

  const navigate = useCallback((newView: ViewState) => {
    // Back to Auto when the scale changes, and only then. A five second bucket
    // that made sense on a day is meaningless on a year, but stepping to the next
    // day is the same width of window and the choice still applies to it.
    const next = newView.scale === view.scale ? granularity : 'auto';

    setView(newView);
    setGranularity(next);
    updateUrl(newView, next);
    setSelectedProcess(null);
    setCustomRange(null);
    setSelectedHourRange(null);
  }, [view.scale, granularity]);

  const changeGranularity = useCallback((id: GranularityId) => {
    setGranularity(id);
    updateGranularityUrl(view, id);
  }, [view]);

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

  const activeRange = customRange ?? getViewRange(view);

  // The floors describe one window, so they stop meaning anything the moment the
  // window changes. Cleared here rather than in the fetch below, because that one
  // also runs on the ninety second refresh and on a granularity change, where the
  // floors are still true and clearing them would flash the notice off.
  //
  // Keyed on the bounds rather than on `view`, because re-selecting the scale
  // already in force builds a fresh but equal object, and a reference comparison
  // would throw the floors away for a window that had not moved.
  useEffect(() => {
    setTimelineDetail(null);
  }, [activeRange.from, activeRange.to]);

  // Answers already fetched for the window on screen. Held in a ref rather than
  // in state because filling it must not cause a render: what is on screen comes
  // from `timeline` and `timelineDetail`, and the cache only decides whether a
  // request has to go out to set them.
  const timelineCache = useRef(new TimelineCache());

  // Emptied whenever an entry in it could have gone out of date, which is when
  // the window moves and when either refresh fires. Ageing individual entries
  // would be the wrong shape: the recording only grows on a refresh, so between
  // two refreshes every held answer is as current as an uncached one would be.
  useEffect(() => {
    timelineCache.current.clear();
  }, [activeRange.from, activeRange.to, refreshKey]);

  // The timeline, on its own. It is the only request a granularity change
  // changes the answer to, so it is the only one a granularity change makes.
  useEffect(() => {
    const { from, to } = activeRange;
    const bucketMs = granularityById(granularity).bucketMs;

    // Two requests can be in flight after a quick second click, and the first
    // is not guaranteed to answer first. Only the newest is allowed to land,
    // otherwise a stale answer overwrites both the series and the floors the
    // picker reads, and the picker starts describing a window nobody is on.
    //
    // The abort below and this sequence number are not the same guard. Aborting
    // stops the server working on an answer nobody will read; the sequence
    // number covers the case where the answer was already on the wire when the
    // abort went out.
    const request = ++latestRequest.current;

    const held = timelineCache.current.get(from, to, bucketMs);
    if (held) {
      applyTimeline(held);
      setTimelineLoading(false);
      return;
    }

    setTimelineLoading(true);
    const controller = new AbortController();

    getTimeline(from, to, bucketMs, controller.signal)
      .then(tl => {
        if (request !== latestRequest.current) return;
        timelineCache.current.set(from, to, bucketMs, tl);
        applyTimeline(tl);
        setTimelineLoading(false);
      })
      .catch(err => {
        // A call-off is not a failure, and neither is an answer that has been
        // overtaken. Either way a newer request is already running and will set
        // the screen; clearing the series here would blank a chart that has an
        // answer, on the strength of a request nobody is waiting for.
        //
        // In practice the sequence check alone catches a supersede today, because
        // React runs the replacement effect before the rejection reaches this
        // handler. That ordering is React's own and not something to depend on,
        // and it does not hold on unmount, where the cleanup aborts with no
        // replacement behind it. Saying what is meant is worth the extra test.
        if (isAbort(err) || request !== latestRequest.current) return;
        applyTimeline(EMPTY_TIMELINE);
        setTimelineLoading(false);
      });

    return () => controller.abort();
  }, [activeRange.from, activeRange.to, refreshKey, granularity, applyTimeline]);

  // The process list, on its own. Nothing about it depends on how finely the
  // chart beside it is divided: /api/processes takes no bucket, and its answer
  // for a window cannot change when one is chosen. It used to be fetched from
  // the same effect as the timeline, so every click on the detail picker re-ran
  // the more expensive of the two queries to arrive back at the rows already on
  // screen.
  useEffect(() => {
    setLoading(true);
    const request = ++latestProcessRequest.current;
    const { from, to } = activeRange;

    getProcesses(from, to, {
      limit: 50,
      sort: processSort,
      q: processFilter || undefined,
    })
      .catch(() => ({ grouped: true, processes: [] }))
      .then(procs => {
        if (request !== latestProcessRequest.current) return;
        setProcesses(procs.processes as ProcessGroupRow[]);
        setLoading(false);
      });
  }, [activeRange.from, activeRange.to, processSort, processFilter, refreshKey]);

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

  function handleNavigateToDay(year: number, month: number, day: number, hour?: number) {
    navigate({ scale: 'day', year, month, day });
    if (hour != null) {
      const dayStart = new Date(year, month - 1, day).getTime();
      const from = dayStart + hour * 3600000;
      const to = from + 3600000;
      setCustomRange({ from, to });
      setSelectedHourRange({ startHour: hour, endHour: hour, from, to });
    }
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
  if (!hasData && rangeLoaded && !wipeOpen) {
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

  const granularityNotice = timelineDetail ? clampNotice(timelineDetail) : null;

  return (
    <div className="app">
      <header className="app-header" role="banner">
        <div className="header-brand">
          <span className="back-slot">
            <button
              className={`back-btn ${selectedProcess ? 'visible' : ''}`}
              onClick={() => window.history.back()}
              aria-label="Back to dashboard"
              title="Back to dashboard"
              tabIndex={selectedProcess ? 0 : -1}
            >
              &larr;
            </button>
          </span>
          <span className="brand-mark" aria-hidden="true" />
          <h1>Telltale</h1>
          <StatusBar />
          {selectedProcess && (
            <div className="header-crumb-current">
              <span className="header-crumb-divider" aria-hidden="true" />
              <span className="header-crumb-name">{drillDownLabel(selectedProcess)}</span>
            </div>
          )}
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
        granularity={granularity}
        onGranularityChange={changeGranularity}
        rangeMs={activeRange.to - activeRange.from}
        servedDetail={timelineDetail}
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
                    <>
                      <p className="granularity-notice" role="status">{granularityNotice}</p>
                      {/*
                        The chart says when it is fetching, rather than the page
                        saying it for the chart. A detail change cannot alter the
                        process table, the summary above it or the heatmap, so
                        putting the whole screen into a loading state for one
                        overstated what was out of date. The chart already drawn
                        stays on screen underneath while the finer one arrives.
                      */}
                      <div className={`timeline-frame${timelineLoading ? ' pending' : ''}`}>
                        {timelineLoading && (
                          <p className="timeline-pending" role="status">Updating chart...</p>
                        )}
                        <Timeline
                          data={timeline}
                          onRangeSelect={handleRangeSelect}
                          thresholds={thresholds}
                        />
                      </div>
                    </>
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
