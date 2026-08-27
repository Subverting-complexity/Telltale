import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

/**
 * The window session token has to survive every rewrite of the address.
 *
 * It is the only thing authorising a delete: `wipeCapture` reads `s` fresh from
 * `location.search` on every call and refuses outright when it is not there. So
 * a navigation that drops it leaves a window where everything on screen still
 * works and only the delete has quietly stopped, which is exactly how it went
 * unnoticed the first time (#126, fixed by 2347bf0). Nothing pinned it
 * afterwards. These tests are that pin.
 *
 * Both routes are covered because they are separate call sites that happen to
 * share a helper: `updateUrl` for a navigation, `updateGranularityUrl` for a
 * change of bucket width. A change to one is not a change to the other.
 */

const getTimeline = vi.fn();

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 7 * 86_400_000, max: Date.now() }),
  getTimeline: (from: number, to: number, bucketMs?: number | null) => {
    getTimeline(from, to, bucketMs);
    return Promise.resolve({
      resolution: 'machine',
      bucketMs: bucketMs ?? 0,
      bucketRequestMs: bucketMs ?? null,
      minBucketMs: 0,
      tierFloorMs: 5_000,
      points: [],
    });
  },
  getProcesses: () => Promise.resolve({ grouped: true, processes: [] }),
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
  isAbort: (error: unknown) => (error as { name?: string })?.name === 'AbortError',
}));

// uPlot draws to a canvas jsdom does not implement, and these tests are about
// the address rather than about what is drawn.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));

/** The token as it is read back out of the address. */
function tokenInUrl(): string | null {
  return new URLSearchParams(window.location.search).get('s');
}

const TOKEN = 'test-session-token';

beforeEach(() => {
  getTimeline.mockClear();
  // Opened the way Telltale opens it: a view, and the token that authorises the
  // delete. A bare address would leave nothing for a navigation to lose.
  window.history.replaceState(null, '', `/?year=2026&month=3&day=14&scale=day&s=${TOKEN}`);
});

describe('the window session token', () => {
  it('survives a change of time scale', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Month' }));

    await waitFor(() =>
      expect(new URLSearchParams(window.location.search).get('scale')).toBe('month'));
    expect(tokenInUrl()).toBe(TOKEN);
  });

  it('survives a change of granularity', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: '1 hour' }));

    // Waiting on the granularity landing in the address rather than on the
    // request, so the assertion below is made against a URL that has actually
    // been rewritten.
    await waitFor(() =>
      expect(new URLSearchParams(window.location.search).get('g')).toBe('1h'));
    expect(tokenInUrl()).toBe(TOKEN);
  });

  it('survives a navigation followed by a granularity change', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Month' }));
    await waitFor(() =>
      expect(new URLSearchParams(window.location.search).get('scale')).toBe('month'));

    await user.click(screen.getByRole('button', { name: '1 hour' }));
    await waitFor(() =>
      expect(new URLSearchParams(window.location.search).get('g')).toBe('1h'));

    expect(tokenInUrl()).toBe(TOKEN);
  });

  it('does not invent a token when the address never carried one', async () => {
    // The standalone viewer is opened without one, and a window that made one up
    // would be asking the application to accept a token it never issued.
    window.history.replaceState(null, '', '/?year=2026&month=3&day=14&scale=day');
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Month' }));

    await waitFor(() =>
      expect(new URLSearchParams(window.location.search).get('scale')).toBe('month'));
    expect(tokenInUrl()).toBeNull();
  });
});
