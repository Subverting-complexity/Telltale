import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Alerts } from './Alerts';
import { getAlerts, getBaselines } from './api';
import type { AlertProcess, AlertsResponse, BaselineData } from './types';

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

/** A process that only the three day period reports, so the two are told apart. */
const otherAlert: AlertProcess = { ...alert, name: 'other.exe' };

/** A promise with its resolve handle kept, so a test can choose when it lands. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

describe('Alerts switching between periods', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getAlerts).mockImplementation(async days => ({
      period: days,
      alerts: days === 1 ? [alert] : [alert, otherAlert],
    }));
    vi.mocked(getBaselines).mockResolvedValue({ baselines: [baseline] });
  });

  function renderAlerts() {
    return render(<Alerts logicalProcessors={LOGICAL_PROCESSORS} onSelectProcess={() => {}} />);
  }

  it('serves a period it has already fetched without asking again', async () => {
    renderAlerts();
    await screen.findByText('app.exe');
    expect(getAlerts).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');
    expect(getAlerts).toHaveBeenCalledTimes(2);

    await userEvent.click(screen.getByRole('tab', { name: '1 day' }));
    await waitFor(() => expect(screen.queryByText('other.exe')).not.toBeInTheDocument());

    // The one day rows are back on screen and no third request was made for
    // them. Before the cache, going back to a period paid for it a second time.
    expect(screen.getByText('app.exe')).toBeInTheDocument();
    expect(getAlerts).toHaveBeenCalledTimes(2);
  });

  it('shows no loading state for a period served from memory', async () => {
    // The one day period answers once and then never again. A second request
    // for it would hang, so if anything is showing a loading state at the end
    // of this test, the cache was not consulted and a request went out.
    let oneDayAnswered = false;
    vi.mocked(getAlerts).mockImplementation(days => {
      if (days !== 1) return Promise.resolve({ period: days, alerts: [otherAlert] });
      if (oneDayAnswered) return new Promise(() => {});
      oneDayAnswered = true;
      return Promise.resolve({ period: 1, alerts: [alert] });
    });

    renderAlerts();
    await screen.findByText('app.exe');

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');

    await userEvent.click(screen.getByRole('tab', { name: '1 day' }));

    expect(screen.queryByText('Loading alerts...')).not.toBeInTheDocument();
    expect(screen.getByText('app.exe')).toBeInTheDocument();
  });

  it('asks about a process baseline once and not once per period', async () => {
    renderAlerts();
    await screen.findByText('app.exe');
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(1));
    expect(vi.mocked(getBaselines).mock.calls[0][0]).toEqual(['app.exe']);

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');

    // A baseline covers a fixed seven days regardless of the period on screen,
    // so the only name worth asking about here is the one newly on the list.
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(2));
    expect(vi.mocked(getBaselines).mock.calls[1][0]).toEqual(['other.exe']);

    await userEvent.click(screen.getByRole('tab', { name: '1 day' }));
    await waitFor(() => expect(screen.queryByText('other.exe')).not.toBeInTheDocument());
    expect(getBaselines).toHaveBeenCalledTimes(2);
  });

  it('does not ask again about a process that reported no baseline', async () => {
    // The endpoint leaves out any process with under 24 hours of history, so
    // 'other.exe' comes back missing rather than empty. Asking is still an
    // answer, and repeating the question on every switch is the bug.
    renderAlerts();
    await screen.findByText('app.exe');
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(1));

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(2));

    await userEvent.click(screen.getByRole('tab', { name: '5 days' }));
    await screen.findByText('other.exe');
    await waitFor(() => expect(getAlerts).toHaveBeenCalledTimes(3));

    expect(getBaselines).toHaveBeenCalledTimes(2);
  });

  it('lets a failed baseline request be retried on a later switch', async () => {
    vi.mocked(getBaselines).mockRejectedValueOnce(new Error('network'));

    renderAlerts();
    await screen.findByText('app.exe');
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(1));

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');

    // A refusal is not a settled answer of "this process has no baseline", so
    // 'app.exe' has to be back in the question rather than written off.
    await waitFor(() => expect(getBaselines).toHaveBeenCalledTimes(2));
    expect(vi.mocked(getBaselines).mock.calls[1][0]).toContain('app.exe');
  });

  it('does not let a slow answer for an old period land over a newer one', async () => {
    const slow = deferred<AlertsResponse>();
    vi.mocked(getAlerts).mockImplementation(days =>
      days === 1
        ? slow.promise
        : Promise.resolve({ period: days, alerts: [otherAlert] }));

    renderAlerts();

    await userEvent.click(screen.getByRole('tab', { name: '3 days' }));
    await screen.findByText('other.exe');

    slow.resolve({ period: 1, alerts: [alert] });
    // Awaited so the component's own handler, queued on this promise before
    // this line was reached, has actually run before anything is asserted.
    await slow.promise;
    await waitFor(() => expect(screen.getByText('other.exe')).toBeInTheDocument());

    // Three days is the period selected, so its rows are the ones that belong
    // on screen. Without the sequence guard the late one day answer overwrote
    // them and left the table disagreeing with the highlighted button.
    expect(screen.getByRole('tab', { name: '3 days' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('other.exe')).toBeInTheDocument();
    expect(screen.queryByText('app.exe')).not.toBeInTheDocument();
  });
});
