import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { HeatmapView } from './Heatmap';
import type { HeatmapBucket } from './types';
import { hueOf, metricHue } from './palette';

// The grid shades one hue by intensity, and that hue is now derived from the
// metric's palette colour rather than written down beside it. This is what
// stops the two drifting: if the derivation is dropped or the wrong metric's
// hue is used, the grid stops matching the line chart for the same metric and
// nothing else would notice.

vi.mock('./api', () => ({
  getHeatmap: vi.fn((_from: number, _to: number, metric: string) =>
    Promise.resolve({
      metric,
      buckets: [
        { dayOffset: 0, hour: 0, avg: 0, peak: 0, count: 1 },
        { dayOffset: 0, hour: 9, avg: 50, peak: 80, count: 6 },
        { dayOffset: 0, hour: 10, avg: 100, peak: 100, count: 6 },
      ] satisfies HeatmapBucket[],
    }),
  ),
}));

const FROM = new Date(2026, 7, 24).getTime();
const TO = FROM + 86400000;

function renderHeatmap() {
  return render(<HeatmapView from={FROM} to={TO} onNavigateToDay={() => {}} />);
}

/**
 * jsdom normalises an hsl() background to rgb(), so the hue has to be read back
 * out of the rendered value rather than matched as text.
 */
function renderedHue(color: string): number {
  const [r, g, b] = color.match(/\d+/g)!.map(Number);
  const hex = [r, g, b].map(n => n.toString(16).padStart(2, '0')).join('');
  return hueOf(`#${hex}`);
}

/** The grid is a corner cell, one day label, then 24 rows of label + cells. */
function cellAt(container: HTMLElement, hour: number): HTMLElement {
  const cells = container.querySelectorAll<HTMLElement>('.heatmap-cell');
  return cells[hour];
}

describe('Heatmap cell colours', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shades each grid with its own metric hue, taken from the palette', async () => {
    const { container } = renderHeatmap();
    await waitFor(() => expect(screen.queryByText(/Loading heatmap/)).not.toBeInTheDocument());

    const sections = container.querySelectorAll<HTMLElement>('.heatmap-section');
    expect(sections).toHaveLength(4);

    const metrics = ['cpu', 'memory', 'disk', 'network'] as const;
    metrics.forEach((metric, i) => {
      // Hour 10 is the busiest bucket, so it is the most saturated cell and
      // certain to carry a colour rather than the empty-cell token.
      const busiest = cellAt(sections[i], 10);
      // Rounding through 8-bit rgb costs a degree either way.
      expect(renderedHue(busiest.style.backgroundColor), metric)
        .toBeCloseTo(metricHue(metric), -0.5);
    });
  });

  it('gives the four metrics four different hues', async () => {
    const { container } = renderHeatmap();
    await waitFor(() => expect(screen.queryByText(/Loading heatmap/)).not.toBeInTheDocument());

    const backgrounds = [...container.querySelectorAll<HTMLElement>('.heatmap-section')]
      .map(section => cellAt(section, 10).style.backgroundColor);

    expect(new Set(backgrounds).size).toBe(4);
  });

  it('leaves an empty hour on the surface token rather than colouring it', async () => {
    const { container } = renderHeatmap();
    await waitFor(() => expect(screen.queryByText(/Loading heatmap/)).not.toBeInTheDocument());

    const section = container.querySelector<HTMLElement>('.heatmap-section')!;
    expect(cellAt(section, 0).style.backgroundColor).toBe('var(--bg-tertiary)');
  });
});
