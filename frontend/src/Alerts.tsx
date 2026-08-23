import { useEffect, useState } from 'react';
import { getAlerts } from './api';
import type { AlertProcess } from './types';
import { formatCpu, formatSize, formatIo, formatDateTime } from './utils';

interface AlertsProps {
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

export function Alerts({ onSelectProcess }: AlertsProps) {
  const [selectedDays, setSelectedDays] = useState(1);
  const [alerts, setAlerts] = useState<AlertProcess[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    getAlerts(selectedDays)
      .then(res => setAlerts(res.alerts))
      .catch(() => setAlerts([]))
      .finally(() => setLoading(false));
  }, [selectedDays]);

  return (
    <section className="alerts-section" aria-label="Problematic processes">
      <h2>Alerts</h2>

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
      ) : alerts.length === 0 ? (
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
                <th scope="col" style={{ textAlign: 'left' }}>Process</th>
                <th scope="col">Avg CPU</th>
                <th scope="col">Peak CPU</th>
                <th scope="col">Peak Memory</th>
                <th scope="col">Total I/O</th>
                <th scope="col">#</th>
                <th scope="col" style={{ textAlign: 'left' }}>Reason</th>
              </tr>
            </thead>
            <tbody>
              {alerts.map(alert => (
                <tr
                  key={alert.name}
                  className="alert-row"
                  onClick={() => onSelectProcess(alert.name)}
                  onKeyDown={e => { if (e.key === 'Enter') onSelectProcess(alert.name); }}
                  tabIndex={0}
                  role="button"
                  aria-label={`View ${alert.name} details`}
                >
                  <td style={{ textAlign: 'left' }}>
                    <span className="alert-process-name">{alert.name}</span>
                    <span className="alert-time-range">
                      {formatDateTime(alert.firstTs)} - {formatDateTime(alert.lastTs)}
                    </span>
                  </td>
                  <td>
                    <span className={`alert-value ${alert.avgCpuPct > 50 ? 'high' : alert.avgCpuPct > 10 ? 'medium' : ''}`}>
                      {formatCpu(alert.avgCpuPct)}
                    </span>
                  </td>
                  <td>{formatCpu(alert.peakCpuPct)}</td>
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
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
