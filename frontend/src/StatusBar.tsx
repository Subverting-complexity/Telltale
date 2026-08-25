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

  const elapsed = health.lastSampleTs > 0 ? Date.now() - health.lastSampleTs : null;
  const ago = elapsed !== null ? formatElapsed(elapsed) + ' ago' : 'never';
  const stale = elapsed !== null && elapsed > 5 * 60 * 1000;
  const stopped = !health.collectorRunning && stale;

  return (
    <div className="status-bar" role="status" aria-live="polite">
      <span
        className={`status-dot ${stopped ? 'stopped' : 'running'}`}
        aria-label={stopped ? 'Collector stopped' : 'Collector running'}
      />
      <span className="status-text">
        {stopped
          ? `Collector stopped (last captured ${ago})`
          : `Last captured ${ago}`}
      </span>
      <span className="status-db">{health.dbSizeMb} MB</span>
    </div>
  );
}
