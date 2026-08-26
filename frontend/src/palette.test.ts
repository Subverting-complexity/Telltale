import { describe, it, expect } from 'vitest';
import {
  CATEGORY_TOKENS,
  CHART_SURFACES,
  METRIC_COLORS,
  METRIC_KEYS,
  SEMANTIC_COLORS,
  SHADOWS,
  categoryCssVar,
  hueOf,
  metricCssVar,
  metricColor,
  metricHue,
  themeStylesheet,
  themeTokens,
} from './palette';
import type { ModeColor, ThemeMode } from './palette';

const MODES: ThemeMode[] = ['light', 'dark'];

describe('metric colours', () => {
  it('gives every metric a value in both modes', () => {
    for (const key of METRIC_KEYS) {
      for (const mode of MODES) {
        expect(metricColor(key, mode), `${key} in ${mode}`).toMatch(/^#[0-9a-f]{6}$/);
      }
    }
  });

  it('uses a distinct colour per metric within a mode', () => {
    for (const mode of MODES) {
      const values = METRIC_KEYS.map(key => metricColor(key, mode));
      expect(new Set(values).size).toBe(METRIC_KEYS.length);
    }
  });

  it('lightens each metric for dark mode rather than reusing the light value', () => {
    for (const key of METRIC_KEYS) {
      expect(metricColor(key, 'dark'), key).not.toBe(metricColor(key, 'light'));
    }
  });

  it('hands components a var() so the DOM follows a theme change on its own', () => {
    expect(metricCssVar('cpu')).toBe('var(--metric-cpu)');
  });
});

describe('metricHue', () => {
  it('derives the heatmap hue from the metric colour, so the two cannot drift', () => {
    for (const key of METRIC_KEYS) {
      expect(metricHue(key)).toBe(hueOf(METRIC_COLORS[key].light));
    }
  });

  it('reads a hue off a hex', () => {
    expect(hueOf('#ff0000')).toBe(0);
    expect(hueOf('#00ff00')).toBe(120);
    expect(hueOf('#0000ff')).toBe(240);
    // Three-digit form, and a grey, which has no hue to report.
    expect(hueOf('#0f0')).toBe(120);
    expect(hueOf('#808080')).toBe(0);
  });

  it('keeps the metrics far enough apart to tell four grids apart', () => {
    const hues = METRIC_KEYS.map(metricHue).sort((a, b) => a - b);
    for (let i = 1; i < hues.length; i++) {
      expect(hues[i] - hues[i - 1], `${hues[i - 1]} vs ${hues[i]}`).toBeGreaterThan(30);
    }
  });
});

describe('token completeness', () => {
  const groups: Record<string, Record<string, ModeColor>> = {
    metrics: METRIC_COLORS,
    semantic: SEMANTIC_COLORS,
    chart: CHART_SURFACES,
    shadows: SHADOWS,
  };

  it('declares both halves of every mode pair', () => {
    for (const [group, entries] of Object.entries(groups)) {
      for (const [name, value] of Object.entries(entries)) {
        expect(value.light, `${group}.${name} light`).toBeTruthy();
        expect(value.dark, `${group}.${name} dark`).toBeTruthy();
      }
    }
  });

  it('emits the same set of custom properties for both modes', () => {
    expect(Object.keys(themeTokens('light')).sort())
      .toEqual(Object.keys(themeTokens('dark')).sort());
  });

  it('includes a property for every metric', () => {
    const tokens = themeTokens('dark');
    for (const key of METRIC_KEYS) {
      expect(tokens[`--metric-${key}`]).toBe(METRIC_COLORS[key].dark);
    }
  });
});

describe('category colours', () => {
  it('points each category at a semantic token rather than a value of its own', () => {
    for (const [category, token] of Object.entries(CATEGORY_TOKENS)) {
      expect(token in SEMANTIC_COLORS, `${category} -> ${token}`).toBe(true);
    }
  });

  it('renders as a var() reference', () => {
    expect(categoryCssVar('services')).toBe('var(--accent)');
    expect(categoryCssVar('applications')).toBe('var(--success)');
    expect(categoryCssVar('system')).toBe('var(--text-muted)');
  });
});

describe('themeStylesheet', () => {
  const css = themeStylesheet();

  it('declares light as the base and dark twice: by preference and by pinned theme', () => {
    expect(css).toContain(':root {');
    expect(css).toContain('@media (prefers-color-scheme: dark)');
    expect(css).toContain(':root:not([data-theme="light"])');
    expect(css).toContain(':root[data-theme="dark"]');
  });

  it('carries every token, in both modes', () => {
    for (const [name, value] of Object.entries(themeTokens('light'))) {
      expect(css, name).toContain(`${name}: ${value};`);
    }
    for (const [name, value] of Object.entries(themeTokens('dark'))) {
      expect(css, name).toContain(`${name}: ${value};`);
    }
  });

  it('includes the tokens derived from other tokens only in the base block', () => {
    expect(css.split('--accent-soft:').length - 1).toBe(1);
    expect(css).toContain('--accent-soft: color-mix(in srgb, var(--accent) 14%, transparent);');
  });
});
