import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

const getTimeline = vi.fn();

/** Counted so a granularity change can be shown not to disturb the process list. */
const getProcesses = vi.fn();

/** The abort signal each timeline request was given, newest last. */
const signals: (AbortSignal | undefined)[] = [];

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

/**
 * Set to make a held response reject the way a real called-off fetch does.
 *
 * Off by default, so the tests about the sequence number keep exercising the
 * sequence number. The two guards answer different situations and each has to be
 * pinned on its own: aborting stops the server working on an answer nobody will
 * read, and the sequence number covers the answer that was already on the wire
 * when the abort went out.
 */
let abortRejects = false;

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 7 * 86_400_000, max: Date.now() }),
  getTimeline: (from: number, to: number, bucketMs?: number | null, signal?: AbortSignal) => {
    getTimeline(from, to, bucketMs);
    signals.push(signal);
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
    return new Promise((resolve, reject) => {
      if (abortRejects && signal) {
        signal.addEventListener('abort', () => {
          const error = new Error('The operation was aborted.');
          error.name = 'AbortError';
          reject(error);
        });
      }
      heldResponses!.push(() => resolve(body()));
    });
  },
  getProcesses: (...args: unknown[]) => {
    getProcesses(...args);
    return Promise.resolve({ grouped: true, processes: [] });
  },
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
  // The real one. It is a pure test on the rejection, and App has to be able to
  // tell a called-off request from a failed one whichever way the api is stood in for.
  isAbort: (error: unknown) => (error as { name?: string })?.name === 'AbortError',
}));

// uPlot draws to a canvas jsdom does not implement, and these tests are about
// what is asked of the server rather than what is drawn.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  getTimeline.mockClear();
  getProcesses.mockClear();
  signals.length = 0;
  minBucketMs = 0;
  tierFloorMs = 5_000;
  servedBucketMs = null;
  heldResponses = null;
  abortRejects = false;
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

describe('the cost of switching granularity', () => {
  it('leaves the process list alone when only the detail changes', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getProcesses).toHaveBeenCalled());
    const before = getProcesses.mock.calls.length;

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    await user.click(screen.getByRole('button', { name: '10 min' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(600_000));

    // /api/processes takes no bucket and its answer for a window cannot change
    // when one is chosen, so asking again is work that arrives back at the rows
    // already on screen. It is also the more expensive of the two queries.
    expect(getProcesses.mock.calls.length).toBe(before);
  });

  it('still refetches the process list when the window moves', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getProcesses).toHaveBeenCalled());
    const before = getProcesses.mock.calls.length;

    await user.click(screen.getByRole('button', { name: 'Previous' }));

    await waitFor(() => expect(getProcesses.mock.calls.length).toBeGreaterThan(before));
  });

  it('serves a granularity already fetched for this window without asking again', async () => {
    const user = userEvent.setup();
    render(<App />);
    // The Auto answer for this window is fetched on mount and held from then on.
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    const before = getTimeline.mock.calls.length;

    await user.click(screen.getByRole('button', { name: 'Auto' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true'));

    expect(getTimeline.mock.calls.length).toBe(before);
  });

  it('keeps the clamp notice for an answer served from memory', async () => {
    const user = userEvent.setup();
    const today = new Date();
    window.history.replaceState(null, '',
      `?year=${today.getFullYear()}&month=${today.getMonth() + 1}&day=${today.getDate()}&scale=day&g=5s`);
    servedBucketMs = 600_000;
    tierFloorMs = 600_000;

    render(<App />);
    await screen.findByText(/Showing 10 minute detail\./);

    // Away and back. The held answer has to carry the floors and the width that
    // was asked for, not just the points, or the notice explaining the widening
    // would disappear the second time the same option was chosen.
    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    const before = getTimeline.mock.calls.length;

    await user.click(screen.getByRole('button', { name: '5 sec' }));

    await screen.findByText(/Showing 10 minute detail\./);
    expect(screen.getByText(/Showing 10 minute detail\./))
      .toHaveTextContent('You asked for 5 second detail');
    expect(getTimeline.mock.calls.length).toBe(before);
  });

  it('asks again for a granularity held against a window that has since moved', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    const afterChoosing = getTimeline.mock.calls.length;

    // Yesterday, then today again. The detail survives a step within the scale,
    // so the second step asks the same question of a window that has moved twice
    // and must not be answered from what was held for the first one.
    await user.click(screen.getByRole('button', { name: 'Previous' }));
    await waitFor(() => expect(getTimeline.mock.calls.length).toBe(afterChoosing + 1));

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(getTimeline.mock.calls.length).toBe(afterChoosing + 2));
  });

  it('asks again after a refresh, so a held answer is never older than the recording', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    const before = getTimeline.mock.calls.length;

    await user.click(screen.getByRole('button', { name: 'Refresh data' }));

    // Nothing about the window changed, so without the cache being emptied the
    // refresh would be answered out of it and would refresh nothing.
    await waitFor(() => expect(getTimeline.mock.calls.length).toBeGreaterThan(before));
    expect(lastRequestedBucket()).toBe(3_600_000);
  });

  it('calls off a timeline request as soon as a newer one is made', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    heldResponses = [];
    await user.click(screen.getByRole('button', { name: '10 min' }));
    await waitFor(() => expect(heldResponses).toHaveLength(1));
    const supersededSignal = signals[signals.length - 1];
    expect(supersededSignal?.aborted).toBe(false);

    await user.click(screen.getByRole('button', { name: '1 hour' }));

    // Telltale answers this window from the same process that is recording the
    // machine, so a query nobody will read is taken out of the sampler rather
    // than merely wasting a round trip.
    await waitFor(() => expect(supersededSignal?.aborted).toBe(true));
    expect(signals[signals.length - 1]?.aborted).toBe(false);
  });

  it('leaves the chart standing when a request is called off rather than failing', async () => {
    const user = userEvent.setup();
    abortRejects = true;
    minBucketMs = 600_000;
    tierFloorMs = 600_000;

    render(<App />);
    // The first answer arrives normally and says 5 sec is out of reach here.
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '5 sec' })).toHaveAttribute('aria-disabled', 'true'));

    heldResponses = [];
    await user.click(screen.getByRole('button', { name: '10 min' }));
    await waitFor(() => expect(heldResponses).toHaveLength(1));

    // Superseding the held request now rejects it, which is what a real
    // called-off fetch does and what the old code never had to handle. An
    // unguarded catch would fall through to the empty answer, whose zeroed
    // floors would offer 5 sec again on the strength of a request nobody is
    // waiting for. Which of the two guards catches it is not pinned here:
    // `isAbort` has its own tests, and the sequence number has the overtake test
    // above.
    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() => expect(heldResponses).toHaveLength(2));

    expect(screen.getByRole('button', { name: '5 sec' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('waits only the chart while a new detail is fetched', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText('Loading...')).not.toBeInTheDocument());

    heldResponses = [];
    await user.click(screen.getByRole('button', { name: '1 hour' }));

    // The chart says it is fetching. The page does not, because a detail change
    // cannot alter the process list, the summary above it or the heatmap.
    await screen.findByText('Updating chart...');
    expect(screen.queryByText('Loading...')).not.toBeInTheDocument();

    await act(async () => { heldResponses![0]!(); });

    await waitFor(() => expect(screen.queryByText('Updating chart...')).not.toBeInTheDocument());
  });

  it('shows no wait at all for a granularity served from memory', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true'));

    // Held from the mount. It arrives in the same tick, so there is nothing to
    // wait for and nothing should say there is.
    await user.click(screen.getByRole('button', { name: 'Auto' }));

    expect(screen.queryByText('Updating chart...')).not.toBeInTheDocument();
  });
});
