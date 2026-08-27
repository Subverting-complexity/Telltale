import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';
import type { ProcessGroupRow } from './types';

const busyProcess: ProcessGroupRow = {
  name: 'chrome.exe',
  cpuPct: 40,
  privateMb: 500,
  ioKb: 100,
  instanceCount: 3,
  path: 'C:\\chrome.exe',
};

vi.mock('./api', () => ({
  getRange: () => Promise.resolve({ min: Date.now() - 86_400_000, max: Date.now() }),
  getTimeline: () => Promise.resolve({
    resolution: '1m', bucketMs: 0, bucketRequestMs: null,
    minBucketMs: 0, tierFloorMs: 5_000, points: [],
  }),
  getProcesses: () => Promise.resolve({ grouped: true, processes: [busyProcess] }),
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
  // The real one. It is a pure test on the rejection, and App has to be able to
  // tell a called-off request from a failed one whichever way the api is stood in for.
  isAbort: (error: unknown) => (error as { name?: string })?.name === 'AbortError',
}));

// These tests are about App's history plumbing around a drill-down, not about
// what ProcessDetail itself renders, so it's stood in for a stub that exposes
// just enough (its type/name and an onBack button) to drive the navigation.
vi.mock('./Timeline', () => ({ Timeline: () => <div data-testid="timeline" /> }));
vi.mock('./Heatmap', () => ({ HeatmapView: () => <div data-testid="heatmap" /> }));
vi.mock('./ProcessDetail', () => ({
  ProcessDetail: ({ onBack, type, name }: { onBack: () => void; type: string; name?: string }) => (
    <div>
      <p>Drill-down: {type === 'group' ? name : 'instance'}</p>
      <button onClick={onBack}>Back</button>
    </div>
  ),
}));
vi.mock('./WipeDataDialog', () => ({
  WipeDataDialog: ({ onWiped }: { onWiped: () => void }) => (
    <button onClick={onWiped}>Confirm wipe</button>
  ),
}));

beforeEach(() => {
  window.history.replaceState(null, '', '/');
});

describe('App drill-down history navigation', () => {
  it('pushes a history entry on drill-in, and the browser Back button returns to the dashboard', async () => {
    const user = userEvent.setup();
    render(<App />);

    const startLength = window.history.length;

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));

    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();
    expect(window.history.length).toBe(startLength + 1);

    window.history.back();

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());
  });

  it('the on-screen Back button goes through browser history rather than only resetting state', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Back' }));

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());

    // Forward should restore it too, since the drill-down was a real history
    // entry rather than state that only ever moved backward.
    window.history.forward();
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();
  });

  it('Escape backs out of a drill-down via history rather than a separate reset path', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();

    await user.keyboard('{Escape}');

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());
  });

  it('ArrowLeft backs out of a drill-down via history, same as Escape', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();

    await user.keyboard('{ArrowLeft}');

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());
  });

  it('shows a back button in the header only once a drill-down is open, and it steps back through history', async () => {
    // The back button stays mounted at all times (a fixed-width slot in the
    // header so nothing else shifts when it appears) — App.css hides it via
    // a CSS class jsdom doesn't apply, so the "hidden" half of this is
    // asserted on the class/tabIndex the CSS keys off, not on presence.
    const user = userEvent.setup();
    render(<App />);

    const headerBack = await screen.findByRole('button', { name: 'Back to dashboard' });
    expect(headerBack).toHaveClass('back-btn');
    expect(headerBack).not.toHaveClass('visible');
    expect(headerBack).toHaveAttribute('tabindex', '-1');

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();

    expect(headerBack).toHaveClass('visible');
    expect(headerBack).toHaveAttribute('tabindex', '0');
    expect(screen.getByText('chrome.exe', { selector: '.header-crumb-name' })).toBeInTheDocument();

    await user.click(headerBack);

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());
    expect(headerBack).not.toHaveClass('visible');
  });

  it('hides the header back button when data is wiped out from under an open drill-down', async () => {
    // The wipe dialog is reachable while a drill-down is open (its header
    // button isn't gated on selectedProcess), so onWiped has to reset the
    // drill-down itself, not just the date range — otherwise the back
    // button would keep pointing at a process whose data no longer exists.
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('listitem', { name: /chrome\.exe/ }));
    expect(await screen.findByText('Drill-down: chrome.exe')).toBeInTheDocument();

    const headerBack = screen.getByRole('button', { name: 'Back to dashboard' });
    expect(headerBack).toHaveClass('visible');

    await user.click(screen.getByRole('button', { name: 'Delete recorded data' }));
    await user.click(await screen.findByRole('button', { name: 'Confirm wipe' }));

    await waitFor(() =>
      expect(screen.queryByText('Drill-down: chrome.exe')).not.toBeInTheDocument());
    expect(headerBack).not.toHaveClass('visible');
  });

  it('leaves plain date-paging ArrowLeft/ArrowRight alone when no drill-down is open', async () => {
    const user = userEvent.setup();
    render(<App />);

    // Wait for the dashboard to settle before reading the starting date, so
    // the "before" snapshot isn't taken mid-load.
    await screen.findByText(/Top Consumers/);
    const before = document.querySelector('.breadcrumb-current')?.textContent;

    await user.keyboard('{ArrowLeft}');

    await waitFor(() => {
      const after = document.querySelector('.breadcrumb-current')?.textContent;
      expect(after).not.toBe(before);
    });

    const afterLeft = document.querySelector('.breadcrumb-current')?.textContent;
    await user.keyboard('{ArrowRight}');

    await waitFor(() => {
      const after = document.querySelector('.breadcrumb-current')?.textContent;
      expect(after).not.toBe(afterLeft);
    });
  });
});
