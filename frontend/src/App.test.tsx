import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

/**
 * The recording as the range endpoint reports it. Wiping everything empties it,
 * which is the whole point of these tests: that transition used to take the
 * dialog down with it.
 */
let range: { min: number | null; max: number | null } = { min: 0, max: 0 };

/**
 * Set to hold the range answer until a test releases it, so the process list can
 * be put ahead of it. That order is the whole question for the empty screen: the
 * process list landing says nothing about whether the recording has a start and
 * an end.
 */
let holdRange = false;

/** Releases a held range answer. Set by the mock once one is being held. */
let releaseRange: (() => void) | null = null;

const wipeCapture = vi.fn();

vi.mock('./api', () => ({
  getRange: () => {
    if (!holdRange) return Promise.resolve(range);
    return new Promise<typeof range>(resolve => { releaseRange = () => resolve(range); });
  },
  getTimeline: () => Promise.resolve({
    resolution: '1m', bucketMs: 0, bucketRequestMs: null,
    minBucketMs: 0, tierFloorMs: 5_000, points: [],
  }),
  getProcesses: () => Promise.resolve({ grouped: true, processes: [] }),
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
  // The real one. It is a pure test on the rejection, and App has to be able to
  // tell a called-off request from a failed one whichever way the api is stood in for.
  isAbort: (error: unknown) => (error as { name?: string })?.name === 'AbortError',
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
  wipeCapture.mockResolvedValue({ rowsDeleted: 1234, bytesFreed: 5 * 1024 * 1024, spacePending: false });
  holdRange = false;
  releaseRange = null;
});

describe('App and the empty screen', () => {
  it('waits for the range request, not for the process list', async () => {
    // The recording is empty, so the empty screen is the right answer once the
    // range endpoint has said so, and the wrong one before it has.
    range = { min: null, max: null };
    holdRange = true;

    render(<App />);

    // The process list has landed, which is what clears the dashboard's loading
    // flag. That flag used to gate this screen, and once the timeline and the
    // process list stopped being fetched together it stopped standing in for the
    // range request: the process list answering says nothing about whether there
    // is a recording at all. Gated on it, the app would announce that nothing has
    // ever been recorded while it is still waiting to be told.
    await waitFor(() => expect(screen.queryByText('Loading...')).not.toBeInTheDocument());
    expect(screen.queryByText('No data yet')).not.toBeInTheDocument();

    await act(async () => { releaseRange!(); });

    expect(await screen.findByText('No data yet')).toBeInTheDocument();
  });
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

  it('does not let the page move the day out from under the dialog', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('button', { name: 'Delete recorded data' }));
    await user.click(await screen.findByRole('radio', { name: /The whole day being viewed/ }));

    const named = screen.getByText(/everything recorded on/).textContent;

    // The page's arrow key shortcuts listen on the window, so with focus on a
    // dialog button they used to step the view to the previous day underneath
    // the dialog, and the day about to be deleted moved with it. The
    // confirmation would then be naming a day the person never chose.
    await user.keyboard('{ArrowLeft}{ArrowLeft}');

    expect(screen.getByText(/everything recorded on/).textContent).toBe(named);

    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    const today = new Date();
    const from = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
    await waitFor(() =>
      expect(wipeCapture).toHaveBeenCalledWith(expect.objectContaining({ from })));
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
