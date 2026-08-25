import { describe, it, expect } from 'vitest';
import type uPlot from 'uplot';
import { pointsConfig, timeAxisValues } from './chartTheme';

function fakeUplot(minSeconds: number, maxSeconds: number): uPlot {
  return { scales: { x: { min: minSeconds, max: maxSeconds } } } as unknown as uPlot;
}

describe('pointsConfig', () => {
  it('shows points at every density, only the size scales down', () => {
    expect(pointsConfig(5000, '#3b82f6').show).toBe(true);
    expect(pointsConfig(500, '#3b82f6').show).toBe(true);
    expect(pointsConfig(50, '#3b82f6').show).toBe(true);
  });

  it('hides points only when there is no data at all', () => {
    expect(pointsConfig(0, '#3b82f6').show).toBe(false);
  });

  it('shrinks the marker as the series gets denser', () => {
    const dense = pointsConfig(5000, '#3b82f6').size as number;
    const sparse = pointsConfig(30, '#3b82f6').size as number;
    expect(dense).toBeLessThan(sparse);
  });
});

// Labels are formatted from local wall-clock time (matching what the person
// looking at the chart sees on their own machine), so every fixture below is
// built from local Date components rather than Date.UTC — the assertions
// need to hold regardless of the timezone the tests run in.
function local(y: number, m: number, d: number, h = 0, min = 0): number {
  return new Date(y, m, d, h, min).getTime() / 1000;
}

describe('timeAxisValues', () => {
  it('formats a single-day span as clock time', () => {
    const min = local(2026, 0, 15, 0, 0);
    const max = local(2026, 0, 15, 20, 0);
    const split = local(2026, 0, 15, 7, 30);

    const [label] = timeAxisValues(fakeUplot(min, max), [split]);
    expect(label).toBe('7:30am');
  });

  it('formats a week/month span as day + month, not clock time', () => {
    const min = local(2026, 0, 1);
    const max = local(2026, 0, 20);
    const split = local(2026, 0, 10);

    const [label] = timeAxisValues(fakeUplot(min, max), [split]);
    expect(label).toBe('Jan 10');
  });

  it('formats a year span as month + year', () => {
    const min = local(2025, 0, 1);
    const max = local(2026, 0, 1);
    const split = local(2025, 7, 1);

    const [label] = timeAxisValues(fakeUplot(min, max), [split]);
    expect(label).toBe('Aug 2025');
  });

  it('picks the label format from the visible span, not the tick spacing', () => {
    // Same single split value, two different visible ranges around it —
    // the format must follow the range, not anything about the tick itself.
    const split = local(2026, 0, 15, 12, 0);

    const dayView = timeAxisValues(
      fakeUplot(local(2026, 0, 15, 0, 0), local(2026, 0, 15, 23, 0)),
      [split],
    )[0];
    const yearView = timeAxisValues(
      fakeUplot(local(2025, 0, 1), local(2026, 0, 1)),
      [split],
    )[0];

    expect(dayView).toBe('12pm');
    expect(yearView).toBe('Jan 2026');
    expect(dayView).not.toBe(yearView);
  });
});
