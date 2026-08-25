import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ProcessDetail } from './ProcessDetail';
import { getProcessGroup, getProcesses } from './api';
import type { ProcessInstanceRow } from './types';

// The component fetches on mount, so the network layer is replaced rather than
// reached. The chart is replaced too: it draws through uPlot onto a canvas,
// which jsdom does not implement, and none of it is what these assert.
vi.mock('./api', () => ({
  getProcessGroup: vi.fn(),
  getProcesses: vi.fn(),
  getProcessDetail: vi.fn(),
}));

vi.mock('./Timeline', () => ({
  ProcessTimeline: () => <div data-testid="process-timeline" />,
}));

/**
 * One instance carrying the values from the two defects this file exists for:
 * 2,223,000 kilobytes of I/O, and a CPU figure above 100 because it is a share
 * of one core rather than of the machine.
 */
const instance: ProcessInstanceRow = {
  id: 1,
  pid: 4321,
  name: 'app.exe',
  path: 'C:\\app.exe',
  cpuPct: 151,
  privateMb: 700,
  ioKb: 2_223_000,
};

function renderGroup() {
  return render(
    <ProcessDetail type="group" name="app.exe" from={0} to={1} onBack={() => {}} />,
  );
}

describe('ProcessDetail instances table', () => {
  beforeEach(() => {
    vi.mocked(getProcessGroup).mockResolvedValue({
      name: 'app.exe', resolution: 'raw', points: [],
    });
    vi.mocked(getProcesses).mockResolvedValue({
      grouped: false, processes: [instance],
    });
  });

  it('reports I/O in the unit it was recorded in', async () => {
    // The regression test PR #96 could not write. formatIo was correct and
    // covered; the defect was this call site handing kilobytes to a helper that
    // takes megabytes, which reported every value 1024 times too large. Only a
    // rendered cell can catch that, which is what this harness is for.
    renderGroup();

    expect(await screen.findByText('2.1 GB')).toBeInTheDocument();

    // What the defect actually rendered. Issue #97 quoted 2167.2 GB, which is
    // slightly out: formatSize(2_223_000) is 2170.9 GB. Asserting the real
    // figure keeps this a guard rather than a line that can never fail.
    expect(screen.queryByText('2170.9 GB')).not.toBeInTheDocument();
  });

  it('says which denominator its CPU column is a share of', async () => {
    // A figure over 100% is correct here and reads as a fault without the
    // heading, because the machine gauge beside it stops at 100.
    renderGroup();

    expect(await screen.findByText(/CPU % of one core/)).toBeInTheDocument();
    expect(screen.getByText('151%')).toBeInTheDocument();
  });

  it('shows the process it was asked for', async () => {
    renderGroup();

    expect(await screen.findByRole('heading', { name: 'app.exe' })).toBeInTheDocument();
    expect(screen.getByText('4321')).toBeInTheDocument();
  });
});
