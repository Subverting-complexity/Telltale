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
  getTimeline: () => Promise.resolve({ resolution: '1m', points: [] }),
  getProcesses: () => Promise.resolve({ grouped: true, processes: [busyProcess] }),
  getHealth: () => Promise.resolve({
    collectorRunning: true, lastSampleTs: 0, dbSizeMb: 1, logicalProcessors: 8,
  }),
  getThresholds: () => Promise.resolve(null),
  getAlerts: () => Promise.resolve({ alerts: [] }),
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
