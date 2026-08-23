import { useEffect, useState } from 'react';
import { getAlerts, getBaselines } from './api';
import type { AlertProcess, BaselineData } from './types';
import { formatCpu, formatSize, formatIo, formatDateTime } from './utils';

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

  useEffect(() => {
    setLoading(true);
    getAlerts(selectedDays)
      .then(res => {
        const filtered = res.alerts.filter(a => a.name.toLowerCase() !== 'idle');
        setAlerts(filtered);
        if (filtered.length > 0) {
          const names = filtered.map(a => a.name);
          return getBaselines(names).then(b => setBaselines(b.baselines)).catch(() => {});
        }
      })
      .catch(() => setAlerts([]))
      .finally(() => setLoading(false));
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

  const sortedAlerts = [...alerts].sort((a, b) => {
    const dir = sortDir === 'desc' ? -1 : 1;
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

  const anomalies = detectAnomalies(alerts, baselines);

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
                      Avg CPU{sortIcon('avgCpu')}
                    </button>
                  </th>
                  <th scope="col">
                    <button className="sort-btn" onClick={() => toggleSort('peakCpu')}>
                      Peak CPU{sortIcon('peakCpu')}
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
                  const hasAnomaly = anomalies.some(a => a.name === alert.name);
                  const normAvgCpu = alert.avgCpuPct / logicalProcessors;
                  const normPeakCpu = alert.peakCpuPct / logicalProcessors;
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
                          {formatCpu(normAvgCpu)}
                        </span>
                      </td>
                      <td>{formatCpu(normPeakCpu)}</td>
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
          <p className="no-data-msg">
            No anomalies detected. Anomaly detection requires at least 24 hours of baseline data.
          </p>
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
                    <td>{anomaly.metric}</td>
                    <td>
                      {anomaly.metric === 'CPU'
                        ? formatCpu(anomaly.current / logicalProcessors)
                        : formatSize(anomaly.current)}
                    </td>
                    <td>
                      {anomaly.metric === 'CPU'
                        ? formatCpu(anomaly.average / logicalProcessors)
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
