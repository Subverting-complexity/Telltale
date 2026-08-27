import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HealthSummary } from './HealthSummary';
import type { TimelinePoint } from './types';
import { metricCssVar } from './palette';

// The tiles and their sparklines take a var() rather than a resolved colour,
// so they follow a theme change without re-rendering. That only works if the
// var reaches a CSS declaration: var() resolves in a style property and not in
// an SVG presentation attribute, so stroke, fill and stop-color have to be set
// through style. Getting that wrong paints nothing at all, which is why it is
// worth a test that renders.

function point(ts: number, cpuPct: number): TimelinePoint {
  return {
    ts,
    cpuPct,
    memoryAvailMb: 4096,
    commitMb: 8192,
    hardFaults: 0,
    diskReadMs: 0,
    diskWriteMs: 0,
    memoryTotalMb: 16384,
    diskBusyPct: 12,
    netKbps: 256,
    gpuBusyPct: null,
  };
}

const TIMELINE = [point(1, 10), point(2, 40), point(3, 25)];

function renderSummary(timeline: TimelinePoint[] = TIMELINE) {
  return render(
    <HealthSummary timeline={timeline} logicalProcessors={16} onScrollTo={() => {}} />,
  );
}

describe('HealthSummary tile colours', () => {
  it('gives each tile its own metric var rather than a resolved colour', () => {
    renderSummary();

    const cpu = screen.getByRole('button', { name: /^CPU:/ });
    const memory = screen.getByRole('button', { name: /^Memory:/ });
    const disk = screen.getByRole('button', { name: /^Disk:/ });
    const network = screen.getByRole('button', { name: /^Network:/ });

    expect(cpu.style.getPropertyValue('--tile-color')).toBe(metricCssVar('cpu'));
    expect(memory.style.getPropertyValue('--tile-color')).toBe(metricCssVar('memory'));
    expect(disk.style.getPropertyValue('--tile-color')).toBe(metricCssVar('disk'));
    expect(network.style.getPropertyValue('--tile-color')).toBe(metricCssVar('network'));
  });

  it('paints the sparkline through style, where a var() actually resolves', () => {
    const { container } = renderSummary();

    const stroked = container.querySelector<SVGPathElement>('.sparkline path[fill="none"]');
    expect(stroked).not.toBeNull();
    expect(stroked!.style.stroke).toBe(metricCssVar('cpu'));
    // Not as an attribute: var() in a presentation attribute is inert.
    expect(stroked!.getAttribute('stroke')).toBeNull();

    const dot = container.querySelector<SVGCircleElement>('.sparkline circle');
    expect(dot!.style.fill).toBe(metricCssVar('cpu'));
    expect(dot!.getAttribute('fill')).toBeNull();

    const stops = container.querySelectorAll<SVGStopElement>('.sparkline stop');
    expect(stops).toHaveLength(8);
    expect(stops[0].style.stopColor).toBe(metricCssVar('cpu'));
    expect(stops[0].getAttribute('stop-color')).toBeNull();
    expect(stops[0].style.stopOpacity).toBe('0.32');
  });
});

describe('HealthSummary reading view', () => {
  it('opens on the latest reading', () => {
    renderSummary();

    expect(screen.getByRole('radio', { name: 'Now' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Over time' })).not.toBeChecked();
    // The last point, 25%, not the mean of 10, 40 and 25.
    expect(screen.getByText('25%')).toBeInTheDocument();
  });

  it('averages the range once Over time is selected', async () => {
    const user = userEvent.setup();
    renderSummary();

    await user.click(screen.getByRole('radio', { name: 'Over time' }));

    // (10 + 40 + 25) / 3 = 25, which the latest reading also happens to be, so
    // the peak is what separates the two views here.
    expect(screen.getByText(/Peak 40%/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^CPU: average/ })).toBeInTheDocument();
  });

  it('distinguishes an average from the last reading when they differ', async () => {
    const user = userEvent.setup();
    renderSummary([point(1, 90), point(2, 90), point(3, 0)]);

    expect(screen.getByText('0%')).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Over time' }));

    // (90 + 90 + 0) / 3 = 60.
    expect(screen.getByText('60%')).toBeInTheDocument();
    expect(screen.queryByText('0%')).not.toBeInTheDocument();
  });

  it('says which reading each tile is answering on', async () => {
    const user = userEvent.setup();
    renderSummary();

    expect(screen.getAllByRole('button', { name: /at the latest reading$/ })).toHaveLength(4);

    await user.click(screen.getByRole('radio', { name: 'Over time' }));

    expect(screen.getAllByRole('button', { name: /across the range shown$/ })).toHaveLength(4);
  });
});
