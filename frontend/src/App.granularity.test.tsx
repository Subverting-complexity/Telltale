import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

const getTimeline = vi.fn();

/** The bucket the most recent timeline request asked for. */
function lastRequestedBucket(): number | null | undefined {
  const calls = getTimeline.mock.calls;
  return calls[calls.length - 1]?.[2];
}

/**
 * The finest bucket the server says the window can serve. Raised in one test to
 * stand for a window old enough that only a rollup tier still holds it.
 */
let minBucketMs = 0;

/** The finest the tiers themselves store, which is what separates the two reasons. */
let tierFloorMs = 5_000;

/** What the server serves, when a test wants it to differ from what was asked. */
let servedBucketMs: number | null = null;

/**
 * Set to make the api mock hand back promises the test resolves by hand, so two
 * requests can be answered out of the order they were made.
 */
let heldResponses: (() => void)[] | null = null;

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 7 * 86_400_000, max: Date.now() }),
  getTimeline: (from: number, to: number, bucketMs?: number | null) => {
    getTimeline(from, to, bucketMs);
    const body = () => ({
      resolution: 'machine',
      // The server answers with what it was asked for unless a test says
      // otherwise, so a widening is never accidental.
      bucketMs: servedBucketMs ?? bucketMs ?? 0,
      bucketRequestMs: bucketMs ?? null,
      minBucketMs,
      tierFloorMs,
      points: [],
    });
    // Read when the response is released rather than when it is asked for, so a
    // test can decide what a held response will turn out to say.
    if (!heldResponses) return Promise.resolve(body());
    return new Promise(resolve => { heldResponses!.push(() => resolve(body())); });
  },
  getProcesses: () => Promise.resolve({ grouped: true, processes: [] }),
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

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  getTimeline.mockClear();
  minBucketMs = 0;
  tierFloorMs = 5_000;
  servedBucketMs = null;
  heldResponses = null;
});

describe('timeline granularity', () => {
  it('asks for nothing in particular until a granularity is chosen', async () => {
    render(<App />);

    await waitFor(() => expect(getTimeline).toHaveBeenCalled());
    expect(getTimeline.mock.calls[0][2]).toBeNull();
    expect(screen.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('asks for the chosen bucket and records it in the URL', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));

    await waitFor(() =>
      expect(lastRequestedBucket()).toBe(3_600_000));
    expect(new URLSearchParams(window.location.search).get('g')).toBe('1h');
  });

  it('returns to Auto when the time scale changes', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true'));

    await user.click(screen.getByRole('tab', { name: 'Month' }));

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true'));
    expect(lastRequestedBucket()).toBeNull();
    expect(new URLSearchParams(window.location.search).get('g')).toBeNull();
  });

  it('ignores an answer overtaken by a newer request', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    // From here the server answers only when this test says so.
    heldResponses = [];

    await user.click(screen.getByRole('button', { name: '10 min' }));
    await waitFor(() => expect(heldResponses).toHaveLength(1));

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(heldResponses).toHaveLength(2));

    // The newer request answers first, and reports a window holding full detail.
    heldResponses![1]!();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '1 min' })).not.toHaveAttribute('aria-disabled', 'true'));

    // Then the overtaken one answers, claiming a much coarser floor. Landing it
    // would withhold 1 min on the strength of a window nobody is looking at.
    minBucketMs = 600_000;
    heldResponses![0]!();

    await waitFor(() =>
      expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true'));
    expect(screen.getByRole('button', { name: '1 min' })).not.toHaveAttribute('aria-disabled', 'true');
  });

  it('holds on to the served detail when the scale already in force is re-selected', async () => {
    const user = userEvent.setup();
    const today = new Date();
    window.history.replaceState(null, '',
      `?year=${today.getFullYear()}&month=${today.getMonth() + 1}&day=${today.getDate()}&scale=day&g=5s`);
    servedBucketMs = 600_000;
    minBucketMs = 600_000;
    tierFloorMs = 600_000;

    render(<App />);
    await screen.findByText(/Showing 10 minute detail\./);
    const before = getTimeline.mock.calls.length;

    // Clicking the tab already selected builds a fresh but equal ViewState. The
    // window has not moved, so nothing should be refetched and nothing the
    // server said about it should be thrown away.
    await user.click(screen.getByRole('tab', { name: 'Day' }));
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });

    expect(getTimeline.mock.calls.length).toBe(before);
    expect(screen.getByText(/Showing 10 minute detail\./)).toBeInTheDocument();
  });

  it('leaves the detail in force usable after the window is narrowed under it', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));

    // Filtering the day down to one hour leaves the hourly bucket wider than the
    // span, but it is still what the chart is drawn at, so the button that says
    // so must not also say it is out of reach.
    await user.click(screen.getByRole('button', { name: /^09:00/ }));

    await waitFor(() => expect(getTimeline.mock.calls.length).toBeGreaterThan(2));
    expect(screen.getByRole('button', { name: '1 hour' })).not.toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('restores a granularity named in the URL', async () => {
    const today = new Date();
    window.history.replaceState(null, '',
      `?year=${today.getFullYear()}&month=${today.getMonth() + 1}&day=${today.getDate()}&scale=day&g=10m`);

    render(<App />);

    await waitFor(() => expect(getTimeline).toHaveBeenCalled());
    expect(getTimeline.mock.calls[0][2]).toBe(600_000);
    expect(screen.getByRole('button', { name: '10 min' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('says so on the chart when the server widens the request', async () => {
    const today = new Date();
    window.history.replaceState(null, '',
      `?year=${today.getFullYear()}&month=${today.getMonth() + 1}&day=${today.getDate()}&scale=day&g=5s`);
    servedBucketMs = 600_000;
    tierFloorMs = 600_000;

    render(<App />);

    // Queried by its text rather than its role: the collector status bar is a
    // live region too, so `role="status"` alone does not identify this one.
    const notice = await screen.findByText(/Showing 10 minute detail\./);
    expect(notice).toHaveTextContent('You asked for 5 second detail');
    expect(notice).toHaveAttribute('role', 'status');
  });

  it('withholds detail the recording no longer holds, and says why without a mouse', async () => {
    const user = userEvent.setup();
    minBucketMs = 600_000;
    tierFloorMs = 600_000;
    render(<App />);

    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    const fiveSeconds = await screen.findByRole('button', { name: '5 sec' });
    await waitFor(() => expect(fiveSeconds).toHaveAttribute('aria-disabled', 'true'));

    // The reason is in the accessible description rather than only the tooltip,
    // because a tooltip reaches neither a keyboard nor a touch user.
    expect(fiveSeconds).toHaveAccessibleDescription(/retained/);

    // And it stays focusable and inert rather than being taken out of the page.
    const before = getTimeline.mock.calls.length;
    await user.click(fiveSeconds);
    expect(getTimeline.mock.calls.length).toBe(before);

    expect(screen.getByRole('button', { name: '10 min' })).not.toHaveAttribute('aria-disabled', 'true');
  });

  it('keeps the chosen detail when stepping within the same scale', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));

    // The previous day is the same width of window, so the reason for resetting
    // to Auto does not apply and the choice has to survive.
    await user.click(screen.getByRole('button', { name: 'Previous' }));

    await waitFor(() => expect(getTimeline.mock.calls.length).toBeGreaterThan(2));
    expect(lastRequestedBucket()).toBe(3_600_000);
    expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true');
  });
});
