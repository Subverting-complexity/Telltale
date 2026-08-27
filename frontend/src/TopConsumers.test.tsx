import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TopConsumers } from './TopConsumers';
import type { ProcessGroupRow } from './types';
import { metricCssVar } from './palette';
import { formatTime } from './utils';

/** Sixteen cores, and a process recorded at 151% of one of them: 9.44% here. */
const LOGICAL_PROCESSORS = 16;

const busyProcess: ProcessGroupRow = {
  name: 'app.exe',
  cpuPct: 151,
  privateMb: 700,
  ioKb: 1024,
  instanceCount: 1,
  path: 'C:\\Program Files\\app\\app.exe',
};

/** What the range averages out to: a different process, and a smaller figure. */
const steadyProcess: ProcessGroupRow = {
  name: 'steady.exe',
  cpuPct: 16,
  privateMb: 120,
  ioKb: 64,
  instanceCount: 1,
  path: 'C:\\Program Files\\steady\\steady.exe',
};

const READING_TS = 1_700_000_123_000;

function renderPanel(overrides: Partial<Parameters<typeof TopConsumers>[0]> = {}) {
  return render(
    <TopConsumers
      processes={[busyProcess]}
      latest={[busyProcess]}
      latestTs={READING_TS}
      logicalProcessors={LOGICAL_PROCESSORS}
      onSelectProcess={() => {}}
      categoryFilter="all"
      {...overrides}
    />,
  );
}

describe('TopConsumers CPU denominator', () => {
  it('says which scale it ranks processes on', () => {
    // The panel converts and always did. What it never said was what the
    // resulting percentage is a percentage of.
    renderPanel();

    expect(screen.getByText(/ranked by CPU, as a share of all cores/))
      .toBeInTheDocument();
  });

  it('shows the converted figure rather than the recorded one', () => {
    renderPanel();

    expect(screen.getByText('9.44%')).toBeInTheDocument();
    expect(screen.queryByText('151%')).not.toBeInTheDocument();
  });
});

describe('TopConsumers metric color', () => {
  it('reuses the line chart\'s CPU color rather than a color of its own', () => {
    renderPanel();

    const panel = screen.getByRole('region', { name: 'Top resource consumers' });
    expect(panel.style.getPropertyValue('--metric-color')).toBe(metricCssVar('cpu'));
  });

  it('switches the metric color when the Memory toggle is selected', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.click(screen.getByRole('radio', { name: 'Memory' }));

    const panel = screen.getByRole('region', { name: 'Top resource consumers' });
    expect(panel.style.getPropertyValue('--metric-color')).toBe(metricCssVar('memory'));
  });
});

describe('TopConsumers reading view', () => {
  it('opens on the latest reading, not on the range', () => {
    renderPanel({ processes: [steadyProcess], latest: [busyProcess] });

    expect(screen.getByRole('radio', { name: 'Now' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Over time' })).not.toBeChecked();
    expect(screen.getByText('app.exe')).toBeInTheDocument();
    expect(screen.queryByText('steady.exe')).not.toBeInTheDocument();
  });

  it('shows the range aggregate once Over time is selected', async () => {
    const user = userEvent.setup();
    renderPanel({ processes: [steadyProcess], latest: [busyProcess] });

    await user.click(screen.getByRole('radio', { name: 'Over time' }));

    expect(screen.getByText('steady.exe')).toBeInTheDocument();
    expect(screen.queryByText('app.exe')).not.toBeInTheDocument();
  });

  it('names the reading it is showing, so Now is not read as live on an old range', () => {
    renderPanel();

    expect(screen.getByText(new RegExp(`at the ${formatTime(READING_TS)} reading`)))
      .toBeInTheDocument();
  });

  it('falls back to naming no particular reading when the range holds none', () => {
    renderPanel({ latestTs: null });

    expect(screen.getByText(/at the most recent reading/)).toBeInTheDocument();
  });

  it('says which aggregate Over time ranks on, rather than calling all three usage', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.click(screen.getByRole('radio', { name: 'Over time' }));
    expect(screen.getByText(/ranked by average CPU/)).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Memory' }));
    expect(screen.getByText(/ranked by peak memory/)).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'I/O' }));
    expect(screen.getByText(/ranked by total I\/O/)).toBeInTheDocument();
  });

  it('keeps the metric choice when the reading view changes', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.click(screen.getByRole('radio', { name: 'Memory' }));
    await user.click(screen.getByRole('radio', { name: 'Over time' }));

    expect(screen.getByRole('radio', { name: 'Memory' })).toBeChecked();
    const panel = screen.getByRole('region', { name: 'Top resource consumers' });
    expect(panel.style.getPropertyValue('--metric-color')).toBe(metricCssVar('memory'));
  });

  it('stays on screen when only the view in force is empty, so the toggle is reachable', () => {
    renderPanel({ processes: [busyProcess], latest: [] });

    expect(screen.getByRole('region', { name: 'Top resource consumers' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Over time' })).toBeInTheDocument();
    expect(screen.getByText(/Nothing was recorded at the most recent reading/))
      .toBeInTheDocument();
  });

  it('goes away only when neither view has anything to show', () => {
    const { container } = renderPanel({ processes: [], latest: [] });

    expect(container).toBeEmptyDOMElement();
  });
});
