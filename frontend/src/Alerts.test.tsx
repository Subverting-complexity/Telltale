import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Alerts } from './Alerts';
import { getAlerts, getBaselines } from './api';
import type { AlertProcess, BaselineData } from './types';

vi.mock('./api', () => ({
  getAlerts: vi.fn(),
  getBaselines: vi.fn(),
}));

/**
 * Sixteen cores, and a process averaging two of them. Every figure below is
 * chosen so the converted value and the recorded value cannot be confused:
 * 200% of one core is 12.5% of the machine, and 400% is 25%.
 */
const LOGICAL_PROCESSORS = 16;

const alert: AlertProcess = {
  name: 'app.exe',
  avgCpuPct: 200,
  peakCpuPct: 400,
  peakMemoryMb: 700,
  totalIoKb: 1024,
  sampleCount: 100,
  instanceCount: 1,
  firstTs: 1_700_000_000_000,
  lastTs: 1_700_000_600_000,
  reasons: ['cpu'],
};

/** Low enough against the alert above to make the CPU ratio an anomaly. */
const baseline: BaselineData = {
  name: 'app.exe',
  avgCpu: 20,
  stddevCpu: 2,
  avgMemoryMb: 650,
  stddevMemoryMb: 10,
  avgIoKb: 1000,
  stddevIoKb: 10,
  dataHours: 168,
};

describe('Alerts CPU denominators', () => {
  beforeEach(() => {
    vi.mocked(getAlerts).mockResolvedValue({ period: 1, alerts: [alert] });
    vi.mocked(getBaselines).mockResolvedValue({ baselines: [baseline] });
  });

  it('says its threshold columns are a share of all cores', async () => {
    // The columns already divided by the core count and said only "Avg CPU",
    // so a reader could not tell them from the per core figures elsewhere on
    // the dashboard. That is the defect issue #94 is about.
    render(<Alerts logicalProcessors={LOGICAL_PROCESSORS} onSelectProcess={() => {}} />);

    expect(await screen.findByRole('columnheader', { name: /Avg CPU % of all cores/ }))
      .toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: /Peak CPU % of all cores/ }))
      .toBeInTheDocument();
  });

  it('converts the threshold figures to a share of all cores', async () => {
    render(<Alerts logicalProcessors={LOGICAL_PROCESSORS} onSelectProcess={() => {}} />);

    // 200% and 400% of one core, over sixteen cores.
    expect(await screen.findByText('12.5%')).toBeInTheDocument();
    expect(screen.getByText('25.0%')).toBeInTheDocument();

    // The recorded per core figures must not appear: they are the same numbers
    // on a different scale, which is exactly the confusion being removed.
    expect(screen.queryByText('200%')).not.toBeInTheDocument();
    expect(screen.queryByText('400%')).not.toBeInTheDocument();
  });

  it('says the anomaly metric is a share of all cores too', async () => {
    // The anomalies tab converts the same way but named the metric only "CPU",
    // so the denominator was stated in one tab and not the other.
    render(<Alerts logicalProcessors={LOGICAL_PROCESSORS} onSelectProcess={() => {}} />);

    await screen.findByRole('columnheader', { name: /Avg CPU % of all cores/ });
    await userEvent.click(screen.getByRole('tab', { name: /Anomalies/ }));

    expect(await screen.findByText('CPU % of all cores')).toBeInTheDocument();
  });
});
