import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';
import type { ProcessGroupRow } from './types';

/**
 * Covers the wiring behind the Now / Over time toggles: that App asks for both
 * the range aggregate and the newest reading, and that Top Consumers opens on
 * the second of the two.
 *
 * The panels' own behaviour is covered in TopConsumers.test.tsx and
 * HealthSummary.test.tsx. What only App can show is that the two requests are
 * made, that they differ in exactly one parameter, and that the answers do not
 * get crossed over on the way to the panel.
 */

const getProcesses = vi.fn();

const READING_TS = 1_700_000_123_000;

function row(name: string, cpuPct: number): ProcessGroupRow {
  return { name, cpuPct, privateMb: 100, ioKb: 10, instanceCount: 1, path: null };
}

/** What the range averages out to. */
const STEADY = row('steady.exe', 24);

/** What was running at the newest reading, which the range average buries. */
const SPIKY = row('spiky.exe', 720);

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 86_400_000, max: Date.now() }),
  getTimeline: () => Promise.resolve({
    resolution: 'machine', bucketMs: 0, bucketRequestMs: null,
    minBucketMs: 0, tierFloorMs: 5_000,
    // Enough points for the health tiles to render, which is what puts their own
    // Now / Over time toggle on the page alongside the panel's.
    points: [1, 2, 3].map(ts => ({
      ts, cpuPct: ts * 10, memoryAvailMb: 4096, commitMb: 8192, hardFaults: 0,
      diskReadMs: 0, diskWriteMs: 0, memoryTotalMb: 16384, diskBusyPct: 5,
      netKbps: 128, gpuBusyPct: null,
    })),
  }),
  getProcesses: (from: number, to: number, opts?: Record<string, unknown>) => {
    getProcesses(from, to, opts);
    return Promise.resolve(opts?.latest
      ? { grouped: true, latestTs: READING_TS, processes: [SPIKY] }
      : { grouped: true, latestTs: null, processes: [STEADY] });
  },
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
}));

// uPlot draws to a canvas jsdom does not implement, and these tests are about
// what is asked of the server rather than what is drawn.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));

/**
 * The Over time control belonging to one panel. Both panels offer one, so a
 * page-wide query would find two and a positional index would silently follow
 * whichever panel happens to render first.
 */
function overTimeIn(panel: string): HTMLElement {
  return within(screen.getByRole('region', { name: panel }))
    .getByRole('radio', { name: 'Over time' });
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  localStorage.clear();
  getProcesses.mockClear();
});

describe('Top consumers reading views', () => {
  it('asks for the newest reading as well as the range', async () => {
    render(<App />);

    await waitFor(() => expect(getProcesses).toHaveBeenCalledTimes(2));

    const [rangeCall, latestCall] = getProcesses.mock.calls;
    expect(rangeCall[2]).not.toHaveProperty('latest', true);
    expect(latestCall[2]).toHaveProperty('latest', true);

    // Same window and same server-side filtering, so the two views describe the
    // same set of processes and differ only in which readings they read.
    expect(latestCall[0]).toBe(rangeCall[0]);
    expect(latestCall[1]).toBe(rangeCall[1]);
    expect(latestCall[2].sort).toBe(rangeCall[2].sort);
  });

  it('opens the panel on the newest reading rather than the range', async () => {
    render(<App />);

    expect(await screen.findByText('spiky.exe')).toBeInTheDocument();
    expect(screen.queryByText('steady.exe')).not.toBeInTheDocument();
  });

  it('hands the range answer to the Over time view, not the latest one', async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByText('spiky.exe');
    await user.click(overTimeIn('Top resource consumers'));

    expect(screen.getByText('steady.exe')).toBeInTheDocument();
    expect(screen.queryByText('spiky.exe')).not.toBeInTheDocument();
  });

  it('leaves the health tiles on Now when Top Consumers moves off it', async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByText('spiky.exe');

    const tiles = overTimeIn('System health summary');
    const consumers = overTimeIn('Top resource consumers');
    await user.click(consumers);

    expect(consumers).toBeChecked();
    expect(tiles).not.toBeChecked();
  });
});
