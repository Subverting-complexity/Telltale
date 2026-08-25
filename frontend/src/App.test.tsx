import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

/**
 * The recording as the range endpoint reports it. Wiping everything empties it,
 * which is the whole point of these tests: that transition used to take the
 * dialog down with it.
 */
let range: { min: number | null; max: number | null } = { min: 0, max: 0 };

const wipeCapture = vi.fn();

vi.mock('./api', () => ({
  getRange: () => Promise.resolve(range),
  getTimeline: () => Promise.resolve({ resolution: '1m', points: [] }),
  getProcesses: () => Promise.resolve({ grouped: true, processes: [] }),
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
  wipeCapture: (...args: unknown[]) => wipeCapture(...args),
  WipeError: class WipeError extends Error {
    constructor(message: string, readonly status: number) { super(message); }
  },
}));

// uPlot draws to a canvas, which jsdom does not implement. These tests are about
// what the app does around the wipe, not about the charts, so the two components
// that own a chart are stood in for.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));

beforeEach(() => {
  const today = new Date();
  range = { min: new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime(),
            max: Date.now() };
  wipeCapture.mockReset();
  wipeCapture.mockResolvedValue({ rowsDeleted: 1234, bytesFreed: 5 * 1024 * 1024 });
});

async function wipeEverything() {
  const user = userEvent.setup();
  render(<App />);

  await user.click(await screen.findByRole('button', { name: 'Delete recorded data' }));
  await user.click(await screen.findByRole('radio', { name: /Everything recorded so far/ }));

  // The recording is empty from here on, which is what the app will see when it
  // asks the range endpoint again after the wipe.
  range = { min: null, max: null };

  await user.click(screen.getByRole('button', { name: 'Delete permanently' }));
}

describe('App and the wipe dialog', () => {
  it('still reports what went after wiping everything empties the recording', async () => {
    await wipeEverything();

    // The regression this guards: wiping everything empties the recording, which
    // sends the app to its "No data yet" screen. That screen replaces the whole
    // page, so React used to tear the dialog down and build a fresh one in the
    // same moment it had something to say, and the person who had just deleted
    // their entire recording was told nothing at all.
    expect(await screen.findByText(/Deleted 1,234 recorded rows/)).toBeInTheDocument();

    // Still there once the range endpoint has answered and the app knows the
    // recording is empty.
    await waitFor(() => expect(wipeCapture).toHaveBeenCalled());
    expect(screen.getByText(/Deleted 1,234 recorded rows/)).toBeInTheDocument();
    expect(screen.queryByText('No data yet')).not.toBeInTheDocument();
  });

  it('shows the empty screen once the dialog is closed', async () => {
    const user = userEvent.setup();
    await wipeEverything();

    await user.click(await screen.findByRole('button', { name: 'Close' }));

    // The screen was waiting behind the dialog rather than being suppressed: the
    // recording really is empty and the app says so as soon as there is nothing
    // on top of it.
    await waitFor(() => expect(screen.getByText('No data yet')).toBeInTheDocument());
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('asks to delete the day it is showing', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('button', { name: 'Delete recorded data' }));
    await user.click(await screen.findByRole('radio', { name: /The whole day being viewed/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    const today = new Date();
    const from = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
    const to = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1).getTime() - 1;

    await waitFor(() =>
      expect(wipeCapture).toHaveBeenCalledWith({ scope: 'range', from, to }));
  });
});
