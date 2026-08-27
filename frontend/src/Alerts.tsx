import { useEffect, useMemo, useRef, useState } from 'react';
import { getAlerts, getBaselines } from './api';
import type { AlertProcess, BaselineData } from './types';
import { formatSize, formatIo, formatDateTime, formatCpuOfAllCores, CPU_OF_ALL_CORES } from './utils';

interface AlertsProps {
  logicalProcessors: number;
  onSelectProcess: (name: string) => void;
}

const PERIODS = [
  { days: 1, label: '1 day' },
  { days: 3, label: '3 days' },
  { days: 5, label: '5 days' },
  { days: 15, label: '15 days' },
  { days: 30, label: '30 days' },
  { days: 60, label: '60 days' },
  { days: 90, label: '90 days' },
  { days: 180, label: '180 days' },
];

type AlertTab = 'threshold' | 'anomalies';
type AlertSortCol = 'name' | 'avgCpu' | 'peakCpu' | 'peakMem' | 'totalIo' | 'count';
type SortDir = 'asc' | 'desc';

/**
 * How long a cached period stays good for.
 *
 * The recorder is still sampling, so "the last 1 day" genuinely changes as time
 * passes and a cached answer cannot be right forever. Ninety seconds is the
 * interval the rest of the dashboard refreshes on, so the Alerts tab is never
 * staler than the page around it, and the rapid back and forth between periods
 * that this cache exists for happens far inside it.
 */
const CACHE_TTL_MS = 90_000;

interface CachedAlerts {
  rows: AlertProcess[];
  fetchedAt: number;
}

interface AnomalyInfo {
  name: string;
  metric: string;
  current: number;
  average: number;
  ratio: number;
  description: string;
}

function detectAnomalies(alerts: AlertProcess[], baselines: BaselineData[]): AnomalyInfo[] {
  const baselineMap = new Map<string, BaselineData>();
  for (const b of baselines) baselineMap.set(b.name, b);

  const anomalies: AnomalyInfo[] = [];
  for (const alert of alerts) {
    const baseline = baselineMap.get(alert.name);
    if (!baseline || baseline.dataHours < 24) continue;

    if (baseline.stddevCpu > 0 && alert.avgCpuPct > baseline.avgCpu + 2 * baseline.stddevCpu) {
      const ratio = baseline.avgCpu > 0 ? alert.avgCpuPct / baseline.avgCpu : 0;
      anomalies.push({
        name: alert.name,
        metric: 'CPU',
        current: alert.avgCpuPct,
        average: baseline.avgCpu,
        ratio,
        description: `CPU ${ratio.toFixed(1)}x above 7-day average`,
      });
    }

    if (baseline.stddevMemoryMb > 0 && alert.peakMemoryMb > baseline.avgMemoryMb + 2 * baseline.stddevMemoryMb) {
      const ratio = baseline.avgMemoryMb > 0 ? alert.peakMemoryMb / baseline.avgMemoryMb : 0;
      anomalies.push({
        name: alert.name,
        metric: 'Memory',
        current: alert.peakMemoryMb,
        average: baseline.avgMemoryMb,
        ratio,
        description: `Memory ${ratio.toFixed(1)}x above 7-day average`,
      });
    }
  }

  return anomalies.sort((a, b) => b.ratio - a.ratio);
}

export function Alerts({ logicalProcessors, onSelectProcess }: AlertsProps) {
  const [selectedDays, setSelectedDays] = useState(1);
  const [alerts, setAlerts] = useState<AlertProcess[]>([]);
  const [baselines, setBaselines] = useState<BaselineData[]>([]);
  const [loading, setLoading] = useState(true);
  const [activeTab, setActiveTab] = useState<AlertTab>('threshold');
  const [sortCol, setSortCol] = useState<AlertSortCol>('avgCpu');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  // Tracked separately from `loading`, which now clears as soon as the alerts
  // land. Without it, the moment between the alerts arriving and the baselines
  // arriving looks exactly like a finished answer of "no anomalies", and the
  // Anomalies tab says there is not enough baseline data when the request for
  // it is still in flight.
  const [baselinesPending, setBaselinesPending] = useState(false);

  // Everything below lives for as long as this tab is mounted and no longer.
  // Nothing here is written to storage: the point is to stop asking for the
  // same answer twice in one sitting, not to remember it between sittings.
  //
  // The two baseline structures are separate on purpose. The map holds the
  // baselines that came back; the set holds every name that has been *asked*
  // about. A process with under 24 hours of rollup data returns no row at all,
  // so without recording that it was asked, it would be requested again on
  // every single period change for as long as the window stayed open.
  const alertsByPeriod = useRef(new Map<number, CachedAlerts>());
  const baselinesByName = useRef(new Map<string, BaselineData>());
  const baselinesAsked = useRef(new Set<string>());

  /** Sequence number of the most recent alerts request, so stale ones can be dropped. */
  const latestRequest = useRef(0);
  const mounted = useRef(true);

  // Reassigned on mount rather than only cleared on unmount, so a remount
  // reuses the same ref rather than inheriting a false left by the last one.
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useEffect(() => {
    const request = ++latestRequest.current;

    /**
     * Fetches baselines for the names nobody has asked about yet.
     *
     * A baseline describes a fixed seven day window that has nothing to do with
     * the period on screen, so a name resolved once stays resolved and later
     * period changes ask for nothing at all.
     */
    function ensureBaselines(rows: AlertProcess[]) {
      const missing = rows
        .map(a => a.name)
        .filter(name => !baselinesAsked.current.has(name));
      if (missing.length === 0) return;

      // Marked before the request goes out rather than after it lands, so a
      // second period change arriving mid-flight does not ask for the same
      // names over again.
      //
      // Nothing here chunks the list, because nothing can overflow the fifty
      // name cap on /api/baselines: these names come from /api/alerts, which
      // is itself LIMIT 50. If that limit is ever raised, the names past the
      // fiftieth would be marked asked here, dropped by the server, and never
      // requested again for the life of the mount.
      for (const name of missing) baselinesAsked.current.add(name);
      setBaselinesPending(true);

      getBaselines(missing)
        .then(res => {
          if (res.baselines.length === 0) return;
          for (const b of res.baselines) baselinesByName.current.set(b.name, b);
          // Deliberately not gated on the sequence number, unlike the alerts
          // below. A baseline covers the same seven days whichever period asked
          // for it, so a late answer is still the right answer, and dropping it
          // would throw away data that is not going to be requested again.
          //
          // Skipped entirely when the answer added nothing, because a fresh
          // array here is a new reference and would rebuild the anomaly list
          // for no change.
          if (mounted.current) setBaselines([...baselinesByName.current.values()]);
        })
        .catch(() => {
          // A failed request is not a settled answer of "this process has no
          // baseline", so let a later switch try these names again.
          for (const name of missing) baselinesAsked.current.delete(name);
        })
        .finally(() => {
          if (mounted.current) setBaselinesPending(false);
        });
    }

    const cached = alertsByPeriod.current.get(selectedDays);
    if (cached && Date.now() - cached.fetchedAt < CACHE_TTL_MS) {
      setAlerts(cached.rows);
      setLoading(false);
      ensureBaselines(cached.rows);
      return;
    }

    setLoading(true);
    getAlerts(selectedDays)
      .then(res => {
        const filtered = res.alerts.filter(a => a.name.toLowerCase() !== 'idle');
        // Cached whether or not this request is still the current one. The rows
        // are a true answer for the period that asked for them either way, and
        // only the state update below has to care which period is on screen.
        alertsByPeriod.current.set(selectedDays, { rows: filtered, fetchedAt: Date.now() });

        // Two requests can be in flight after a quick second click, and the
        // first is not guaranteed to answer first. Only the newest is allowed
        // to land, otherwise the table shows one period's rows underneath
        // another period's highlighted button.
        if (request !== latestRequest.current) return;
        setAlerts(filtered);
        setLoading(false);
        ensureBaselines(filtered);
      })
      .catch(() => {
        if (request !== latestRequest.current) return;
        setAlerts([]);
        setLoading(false);
      });
  }, [selectedDays]);

  function toggleSort(col: AlertSortCol) {
    if (sortCol === col) {
      setSortDir(d => d === 'desc' ? 'asc' : 'desc');
    } else {
      setSortCol(col);
      setSortDir('desc');
    }
  }

  function sortIcon(col: AlertSortCol) {
    if (sortCol !== col) return '';
    return sortDir === 'desc' ? ' ▼' : ' ▲';
  }

  // The three derivations below are memoised on what they actually read.
  // Switching between the Thresholds and Anomalies tabs changes neither the
  // alerts nor the baselines, and both tabs are drawn from data already in
  // hand, so that switch should cost a render and nothing else. Sorting is the
  // same story for the anomaly list, which does not depend on the sort at all.
  const sortedAlerts = useMemo(() => {
    const dir = sortDir === 'desc' ? -1 : 1;
    return [...alerts].sort((a, b) => {
      switch (sortCol) {
        case 'name': return a.name.localeCompare(b.name) * dir;
        case 'avgCpu': return (a.avgCpuPct - b.avgCpuPct) * dir;
        case 'peakCpu': return (a.peakCpuPct - b.peakCpuPct) * dir;
        case 'peakMem': return (a.peakMemoryMb - b.peakMemoryMb) * dir;
        case 'totalIo': return (a.totalIoKb - b.totalIoKb) * dir;
        case 'count': return (a.instanceCount - b.instanceCount) * dir;
        default: return 0;
      }
    });
  }, [alerts, sortCol, sortDir]);

  const anomalies = useMemo(() => detectAnomalies(alerts, baselines), [alerts, baselines]);

  // The threshold table asks this once per row. Scanning the anomaly list for
  // each one made the lookup grow with the product of the two lists.
  const anomalousNames = useMemo(
    () => new Set(anomalies.map(a => a.name)),
    [anomalies],
  );

  return (
    <section className="alerts-section" aria-label="Problematic processes">
      <div className="alerts-header">
        <h2>Alerts</h2>
        <div className="alerts-tabs" role="tablist" aria-label="Alert type">
          <button
            role="tab"
            aria-selected={activeTab === 'threshold'}
            className={`toggle-btn ${activeTab === 'threshold' ? 'active' : ''}`}
            onClick={() => setActiveTab('threshold')}
          >
            Thresholds
          </button>
          <button
            role="tab"
            aria-selected={activeTab === 'anomalies'}
            className={`toggle-btn ${activeTab === 'anomalies' ? 'active' : ''}`}
            onClick={() => setActiveTab('anomalies')}
          >
            Anomalies{anomalies.length > 0 ? ` (${anomalies.length})` : ''}
          </button>
        </div>
      </div>

      <div className="alerts-periods" role="tablist" aria-label="Select time period">
        {PERIODS.map(p => (
          <button
            key={p.days}
            role="tab"
            aria-selected={selectedDays === p.days}
            className={`period-btn ${selectedDays === p.days ? 'active' : ''}`}
            onClick={() => setSelectedDays(p.days)}
          >
            {p.label}
          </button>
        ))}
      </div>

      {loading ? (
        <p className="loading">Loading alerts...</p>
      ) : activeTab === 'threshold' ? (
        sortedAlerts.length === 0 ? (
          <p className="no-data-msg">
            No problematic processes detected in the last {PERIODS.find(p => p.days === selectedDays)?.label ?? `${selectedDays} days`}.
          </p>
        ) : (
          <div className="alerts-table-wrapper" role="region" aria-label="Alerts table" tabIndex={0}>
            <table className="alerts-table">
              <caption className="sr-only">
                Problematic processes over the last {selectedDays} day{selectedDays !== 1 ? 's' : ''}
              </caption>
              <thead>
                <tr>
                  <th scope="col" style={{ textAlign: 'left' }}>
                    <button className="sort-btn" onClick={() => toggleSort('name')}>
                      Process{sortIcon('name')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('avgCpu')}>
                      Avg {CPU_OF_ALL_CORES}{sortIcon('avgCpu')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('peakCpu')}>
                      Peak {CPU_OF_ALL_CORES}{sortIcon('peakCpu')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('peakMem')}>
                      Peak Memory{sortIcon('peakMem')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('totalIo')}>
                      Total I/O{sortIcon('totalIo')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('count')}>
                      #{sortIcon('count')}
                    </button>
                  </th>
                  <th scope="col" style={{ textAlign: 'left' }}>Reason</th>
                </tr>
              </thead>
              <tbody>
                {sortedAlerts.map(alert => {
                  const hasAnomaly = anomalousNames.has(alert.name);
                  // Kept as a number for the threshold classes below, which
                  // compare against a share of the whole machine. The figures
                  // shown go through formatCpuOfAllCores so there is one
                  // conversion rather than one per call site, and this falls back
                  // the same way it does, so the colour and the number cannot
                  // disagree about which scale they are on.
                  const normAvgCpu = logicalProcessors >= 1
                    ? alert.avgCpuPct / logicalProcessors
                    : alert.avgCpuPct;
                  return (
                    <tr
                      key={alert.name}
                      className={`alert-row ${hasAnomaly ? 'has-anomaly' : ''}`}
                      onClick={() => onSelectProcess(alert.name)}
                      onKeyDown={e => { if (e.key === 'Enter') onSelectProcess(alert.name); }}
                      tabIndex={0}
                      role="button"
                      aria-label={`View ${alert.name} details`}
                    >
                      <td style={{ textAlign: 'left' }}>
                        <span className="alert-process-name">
                          {hasAnomaly && <span className="anomaly-indicator" title="Unusual activity detected">!</span>}
                          {alert.name}
                        </span>
                        <span className="alert-time-range">
                          {formatDateTime(alert.firstTs)} - {formatDateTime(alert.lastTs)}
                        </span>
                      </td>
                      <td>
                        <span className={`alert-value ${normAvgCpu > 50 ? 'high' : normAvgCpu > 10 ? 'medium' : ''}`}>
                          {formatCpuOfAllCores(alert.avgCpuPct, logicalProcessors)}
                        </span>
                      </td>
                      <td>{formatCpuOfAllCores(alert.peakCpuPct, logicalProcessors)}</td>
                      <td>
                        <span className={`alert-value ${alert.peakMemoryMb > 2048 ? 'high' : alert.peakMemoryMb > 500 ? 'medium' : ''}`}>
                          {formatSize(alert.peakMemoryMb)}
                        </span>
                      </td>
                      <td>{formatIo(alert.totalIoKb)}</td>
                      <td>{alert.instanceCount}</td>
                      <td style={{ textAlign: 'left' }}>
                        <ul className="alert-reasons">
                          {alert.reasons.map((reason, i) => (
                            <li key={i} className="alert-reason-tag">{reason}</li>
                          ))}
                        </ul>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )
      ) : (
        anomalies.length === 0 ? (
          // The alerts and the baselines land separately, so an empty anomaly
          // list means "none found" only once the baselines are actually in.
          // Saying there is not enough baseline data while still waiting for it
          // states as settled something that is not yet known.
          baselinesPending ? (
            <p className="loading">Loading baselines...</p>
          ) : (
            <p className="no-data-msg">
              No anomalies detected. Anomaly detection requires at least 24 hours of baseline data.
            </p>
          )
        ) : (
          <div className="alerts-table-wrapper" role="region" aria-label="Anomalies table" tabIndex={0}>
            <table className="alerts-table">
              <caption className="sr-only">Anomalous process behaviour</caption>
              <thead>
                <tr>
                  <th scope="col" style={{ textAlign: 'left' }}>Process</th>
                  <th scope="col">Metric</th>
                  <th scope="col">Current</th>
                  <th scope="col">7-day Avg</th>
                  <th scope="col">Ratio</th>
                  <th scope="col" style={{ textAlign: 'left' }}>Description</th>
                </tr>
              </thead>
              <tbody>
                {anomalies.map((anomaly, i) => (
                  <tr
                    key={`${anomaly.name}-${anomaly.metric}-${i}`}
                    className="alert-row anomaly-row"
                    onClick={() => onSelectProcess(anomaly.name)}
                    onKeyDown={e => { if (e.key === 'Enter') onSelectProcess(anomaly.name); }}
                    tabIndex={0}
                    role="button"
                    aria-label={`View ${anomaly.name} details`}
                  >
                    <td style={{ textAlign: 'left' }}>
                      <span className="alert-process-name">{anomaly.name}</span>
                    </td>
                    <td>{anomaly.metric === 'CPU' ? CPU_OF_ALL_CORES : anomaly.metric}</td>
                    <td>
                      {anomaly.metric === 'CPU'
                        ? formatCpuOfAllCores(anomaly.current, logicalProcessors)
                        : formatSize(anomaly.current)}
                    </td>
                    <td>
                      {anomaly.metric === 'CPU'
                        ? formatCpuOfAllCores(anomaly.average, logicalProcessors)
                        : formatSize(anomaly.average)}
                    </td>
                    <td>
                      <span className="anomaly-ratio">{anomaly.ratio.toFixed(1)}x</span>
                    </td>
                    <td style={{ textAlign: 'left' }}>
                      <span className="anomaly-tag">{anomaly.description}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      )}
    </section>
  );
}
