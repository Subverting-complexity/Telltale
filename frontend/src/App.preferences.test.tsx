import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';
import { VIEW_PREFERENCES_KEY, loadViewPreferences, saveViewPreferences } from './viewPreferences';
import type { ViewPreferences } from './viewPreferences';

const getTimeline = vi.fn();

/** The bucket the most recent timeline request asked for. */
function lastRequestedBucket(): number | null | undefined {
  const calls = getTimeline.mock.calls;
  return calls[calls.length - 1]?.[2];
}

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 30 * 86_400_000, max: Date.now() }),
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
}));

// Both draw to a canvas jsdom does not implement, and these tests are about
// which view opens rather than what is drawn in it.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));

/** A saved entry, complete, with a scale and granularity that pair. */
function saved(overrides: Partial<ViewPreferences> = {}) {
  saveViewPreferences({
    scale: 'month',
    granularity: '1h',
    granularityScale: 'month',
    tab: 'processes',
    heatmap: false,
    sort: 'memory',
    category: 'applications',
    ...overrides,
  });
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  localStorage.clear();
  getTimeline.mockClear();
});

describe('restoring the view a window was left on', () => {
  it('opens on the saved scale, tab, sort and category', async () => {
    saved();

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Month' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Processes' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('button', { name: 'Applications' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /^Memory/ })).toHaveTextContent('▼');
  });

  it('opens on the current period rather than the one that was on screen', async () => {
    saved({ scale: 'day', granularityScale: 'day' });

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    // The scale comes back; the date does not. A window reopening on a day
    // weeks old would read as a recorder that had stopped.
    const now = new Date();
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    expect(getTimeline.mock.calls[0][0]).toBe(startOfToday);
  });

  it('opens on the saved granularity', async () => {
    saved();

    render(<App />);

    await waitFor(() => expect(lastRequestedBucket()).toBe(3_600_000));
    expect(screen.getByRole('button', { name: '1 hour' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('falls back to Auto when the granularity belonged to another scale', async () => {
    // Only reachable from an entry written by a build that paired them
    // differently, but the pairing is the whole reason the scale is stored
    // alongside, so the mismatch has to be handled rather than trusted.
    saved({ scale: 'day', granularity: '1d', granularityScale: 'year' });

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(lastRequestedBucket()).toBeNull();
    expect(screen.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('opens on the heatmap when that is what was showing', async () => {
    // On the Overview tab, which is where the chart and the heatmap live, and at
    // Month scale, which is one of the scales that offers the choice at all.
    saved({ heatmap: true, tab: 'overview' });

    render(<App />);

    await waitFor(() => expect(screen.getByTestId('heatmap')).toBeInTheDocument());
  });

  it('does not restore the process filter', async () => {
    // Not saved in the first place. Asserted from the rendered field, because
    // what matters is that the list is unfiltered on open, however the entry
    // came to exist.
    saved();
    localStorage.setItem(VIEW_PREFERENCES_KEY, JSON.stringify({
      ...loadViewPreferences(), filter: 'chrome',
    }));

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('searchbox', { name: /filter processes/i })).toHaveValue('');
  });

  it('opens on the defaults when nothing has been saved', async () => {
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Day' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');
    expect(lastRequestedBucket()).toBeNull();
  });

  it('renders normally when the saved entry is unreadable', async () => {
    localStorage.setItem(VIEW_PREFERENCES_KEY, '{ not json');

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Day' })).toHaveAttribute('aria-selected', 'true');
  });

  it('renders normally when the store refuses to hand the entry over', async () => {
    // Scoped to this key rather than refusing everything, because the theme is
    // read from the same store without a guard of its own (#150). That is out of
    // this story's scope; refusing every key here would test that gap instead of
    // this one. Once #150 is fixed this can refuse every key.
    const real = Storage.prototype.getItem;
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      if (key === VIEW_PREFERENCES_KEY) throw new DOMException('The operation is insecure.');
      return real.call(this, key);
    });

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Day' })).toHaveAttribute('aria-selected', 'true');
    vi.restoreAllMocks();
  });
});

describe('the URL takes precedence over what was saved', () => {
  it('uses the range in the URL and ignores the saved scale', async () => {
    saved({ scale: 'month', granularityScale: 'month' });
    window.history.replaceState(null, '', '/?year=2026&month=3&day=14&scale=day');

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Day' })).toHaveAttribute('aria-selected', 'true');
    expect(getTimeline.mock.calls[0][0]).toBe(new Date(2026, 2, 14).getTime());
  });

  it('drops the saved granularity when the URL carries a range without one', async () => {
    // A link someone was given describes a particular thing to look at. Applying
    // a habit from this machine on top of it would change what they were sent.
    saved({ granularity: '1h', granularityScale: 'month' });
    window.history.replaceState(null, '', '/?year=2026&month=3&scale=month');

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(lastRequestedBucket()).toBeNull();
  });

  it('uses the granularity in the URL', async () => {
    saved({ granularity: '1h', granularityScale: 'month' });
    window.history.replaceState(null, '', '/?year=2026&month=3&scale=month&g=1d');

    render(<App />);

    await waitFor(() => expect(lastRequestedBucket()).toBe(86_400_000));
  });

  it('still restores the tab and the sort, which the URL never carries', async () => {
    saved({ tab: 'processes', sort: 'io' });
    window.history.replaceState(null, '', '/?year=2026&month=3&day=14&scale=day');

    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    expect(screen.getByRole('tab', { name: 'Processes' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('button', { name: /^I\/O/ })).toHaveTextContent('▼');
  });
});

describe('saving the view as it changes', () => {
  it('records the tab that was moved to', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Alerts' }));

    await waitFor(() => expect(loadViewPreferences().tab).toBe('alerts'));
  });

  it('records the scale and the granularity chosen under it together', async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Month' }));
    await user.click(screen.getByRole('button', { name: '1 hour' }));

    await waitFor(() => {
      const stored = loadViewPreferences();
      expect(stored.scale).toBe('month');
      expect(stored.granularity).toBe('1h');
      expect(stored.granularityScale).toBe('month');
    });
  });

  it('never writes the process filter text, whatever is typed into it', async () => {
    // The read side is covered above. This is the write side, and it is the one
    // that would fail if a field were ever added to the saved object: what
    // someone searched for is exactly the part of the view that stays private.
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(getTimeline).toHaveBeenCalled());

    await user.click(screen.getByRole('tab', { name: 'Processes' }));
    await user.type(screen.getByRole('searchbox', { name: /filter processes/i }), 'keepass');

    await waitFor(() => expect(loadViewPreferences().tab).toBe('processes'));
    const stored = localStorage.getItem(VIEW_PREFERENCES_KEY) ?? '';
    expect(stored).not.toContain('keepass');
    expect(stored).not.toContain('filter');
  });

  it('writes an entry on the first open, so the next one has something to read', async () => {
    render(<App />);

    await waitFor(() => expect(localStorage.getItem(VIEW_PREFERENCES_KEY)).not.toBeNull());
  });
});
