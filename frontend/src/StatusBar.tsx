import { useEffect, useState } from 'react';
import { getHealth } from './api';
import type { HealthResponse } from './types';
import { formatElapsed } from './utils';

export function StatusBar() {
  const [health, setHealth] = useState<HealthResponse | null>(null);

  useEffect(() => {
    const load = () => { getHealth().then(setHealth).catch(() => {}); };
    load();
    const id = setInterval(load, 10000);
    return () => clearInterval(id);
  }, []);

  if (!health) return null;

  const ago = health.lastSampleTs > 0
    ? formatElapsed(Date.now() - health.lastSampleTs) + ' ago'
    : 'never';

  return (
    <div className="status-bar" role="status" aria-live="polite">
      <span
        className={`status-dot ${health.collectorRunning ? 'running' : 'stopped'}`}
        aria-label={health.collectorRunning ? 'Collector running' : 'Collector stopped'}
      />
      <span className="status-text">
        {health.collectorRunning
          ? `Collecting (${health.processCount} processes, last sample ${ago})`
          : `Collector stopped (last sample ${ago})`}
      </span>
      <span className="status-db">{health.dbSizeMb} MB</span>
    </div>
  );
}
