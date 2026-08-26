// The single source of truth for every colour in the frontend.
//
// Before this file existed, "what colour is CPU" was answered independently in
// four places: --bar-cpu in App.css, CHART_COLORS in chartTheme.ts, a bare HSL
// hue in Heatmap.tsx, and a category map inside ProcessTable.tsx. They agreed
// in light mode by coincidence and disagreed in dark mode, because only the CSS
// half had dark values at all.
//
// Everything now reads from here. CSS gets these as custom properties, emitted
// by theme.ts; canvas charts, which cannot read a CSS variable, resolve the
// value for the current mode directly.
//
// To re-theme the application, change this file. Nothing else declares a colour.

import type { ProcessCategory } from './utils';

export type ThemeMode = 'light' | 'dark';

// A colour that differs between the two themes. Both halves are always
// declared, so a token can never be defined for one mode and missing in the
// other, which is exactly how the chart colours drifted from the CSS ones.
export interface ModeColor {
  readonly light: string;
  readonly dark: string;
}

export type MetricKey = 'cpu' | 'memory' | 'disk' | 'network' | 'io';

// Declaration order is also the order comparison series are assigned colours.
export const METRIC_KEYS: readonly MetricKey[] = ['cpu', 'memory', 'disk', 'network', 'io'];

// One entry per measured resource. Every line, bar, sparkline, tile accent,
// legend dot and heatmap cell for that resource comes from its entry.
export const METRIC_COLORS: Record<MetricKey, ModeColor> = {
  cpu: { light: '#3b82f6', dark: '#60a5fa' },
  memory: { light: '#10b981', dark: '#34d399' },
  disk: { light: '#f59e0b', dark: '#fbbf24' },
  network: { light: '#8b5cf6', dark: '#a78bfa' },
  io: { light: '#ef4444', dark: '#f87171' },
};

// Surfaces, text, borders and status colours. These are the values App.css
// declared in its :root blocks; they have not been retuned, only moved.
export const SEMANTIC_COLORS = {
  'bg-primary': { light: '#ffffff', dark: '#0a0f1a' },
  'bg-secondary': { light: '#f7f8fb', dark: '#121a2b' },
  'bg-tertiary': { light: '#eef0f5', dark: '#1a2338' },
  'surface-toolbar': { light: '#ffffff', dark: '#0d1422' },
  'text-primary': { light: '#161c2c', dark: '#eef1f8' },
  'text-secondary': { light: '#5b6478', dark: '#9aa4c0' },
  'text-muted': { light: '#94a0b8', dark: '#6b7590' },
  border: { light: '#e2e5ee', dark: '#232c42' },
  'border-strong': { light: '#ccd1e0', dark: '#34405e' },
  accent: { light: '#3b82f6', dark: '#60a5fa' },
  'accent-hover': { light: '#2563eb', dark: '#93bbfd' },
  // Status colours deliberately do not shift between modes, which is what
  // App.css already did. They read as "good / caution / bad" rather than as a
  // series, so they are not part of the metric ramp even where the hue matches.
  success: { light: '#10b981', dark: '#10b981' },
  warning: { light: '#f59e0b', dark: '#f59e0b' },
  danger: { light: '#ef4444', dark: '#ef4444' },
} as const satisfies Record<string, ModeColor>;

export type SemanticKey = keyof typeof SEMANTIC_COLORS;

// Chart furniture: the axes, grid and annotation colours the line charts paint
// around a series. uPlot draws onto a canvas, so it needs a resolved value
// rather than a var(); these are the same greys the charts already used.
export const CHART_SURFACES = {
  'chart-axis': { light: '#6b7280', dark: '#9ca3af' },
  'chart-grid': { light: '#e5e7eb', dark: '#374151' },
  'chart-bg': { light: '#ffffff', dark: '#111827' },
  'chart-threshold-line': { light: 'rgba(107,114,128,0.3)', dark: 'rgba(156,163,175,0.4)' },
  'chart-threshold-text': { light: '#9ca3af', dark: '#9ca3af' },
  'chart-mean-line': { light: 'rgba(217,119,6,0.6)', dark: 'rgba(251,191,36,0.7)' },
  'chart-mean-text': { light: '#d97706', dark: '#fbbf24' },
  'chart-trend-line': { light: 'rgba(219,39,119,0.5)', dark: 'rgba(244,114,182,0.6)' },
} as const satisfies Record<string, ModeColor>;

export type ChartKey = keyof typeof CHART_SURFACES;

// Shadows carry a colour, so they belong here rather than with the geometry
// tokens (--radius and friends) that stay in App.css.
export const SHADOWS: Record<string, ModeColor> = {
  'shadow-sm': {
    light: '0 1px 3px rgba(20, 25, 40, 0.07)',
    dark: '0 2px 6px rgba(0, 0, 0, 0.3)',
  },
  'shadow-card': {
    light: '0 1px 2px rgba(20, 25, 40, 0.06), 0 0 0 1px var(--border)',
    dark: '0 1px 2px rgba(0, 0, 0, 0.35), 0 0 0 1px var(--border)',
  },
  // A pressed or selected control, a tile lifting on hover, and a tooltip.
  // These were five separate rgba(0, 0, 0, ...) values in App.css, all
  // unchanged in dark mode where black on a dark surface reads as nothing.
  'shadow-control': {
    light: '0 1px 2px color-mix(in srgb, #000 10%, transparent)',
    dark: '0 1px 2px color-mix(in srgb, #000 45%, transparent)',
  },
  'shadow-raised': {
    light: '0 4px 12px color-mix(in srgb, #000 12%, transparent)',
    dark: '0 4px 12px color-mix(in srgb, #000 50%, transparent)',
  },
  'shadow-popover': {
    light: '0 2px 8px color-mix(in srgb, #000 15%, transparent)',
    dark: '0 2px 8px color-mix(in srgb, #000 55%, transparent)',
  },
  'shadow-overlay': {
    light: '0 12px 32px color-mix(in srgb, #000 25%, transparent)',
    dark: '0 12px 32px color-mix(in srgb, #000 35%, transparent)',
  },
  scrim: {
    light: 'color-mix(in srgb, #000 45%, transparent)',
    dark: 'color-mix(in srgb, #000 55%, transparent)',
  },
};

// Process categories are coloured by role, not by a palette of their own, so
// they point at a semantic token rather than repeating a value.
export const CATEGORY_TOKENS: Record<ProcessCategory, SemanticKey> = {
  system: 'text-muted',
  services: 'accent',
  applications: 'success',
};

// Tokens whose value is expressed in terms of another token, so they resolve
// per mode without being declared twice.
const DERIVED_TOKENS: Record<string, string> = {
  'accent-soft': 'color-mix(in srgb, var(--accent) 14%, transparent)',
  'on-accent': '#ffffff',
  // A solid red button darkening under the pointer. Expressed against the
  // token rather than the value, so it follows --danger wherever it goes.
  'danger-hover': 'color-mix(in srgb, var(--danger) 85%, #000)',
  'focus-ring': '0 0 0 2px var(--accent)',
};

export function metricVar(key: MetricKey): string {
  return `--metric-${key}`;
}

// The form a component passes to the DOM: a var() reference, not a hex, so the
// element follows a theme change with no re-render and no JS.
export function metricCssVar(key: MetricKey): string {
  return `var(${metricVar(key)})`;
}

export function metricColor(key: MetricKey, mode: ThemeMode): string {
  return METRIC_COLORS[key][mode];
}

export function chartColor(key: ChartKey, mode: ThemeMode): string {
  return CHART_SURFACES[key][mode];
}

export function categoryCssVar(category: ProcessCategory): string {
  return `var(--${CATEGORY_TOKENS[category]})`;
}

export function hueOf(hex: string): number {
  const trimmed = hex.replace('#', '');
  const full = trimmed.length === 3
    ? trimmed[0] + trimmed[0] + trimmed[1] + trimmed[1] + trimmed[2] + trimmed[2]
    : trimmed;
  const r = parseInt(full.slice(0, 2), 16) / 255;
  const g = parseInt(full.slice(2, 4), 16) / 255;
  const b = parseInt(full.slice(4, 6), 16) / 255;

  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const delta = max - min;
  if (delta === 0) return 0;

  const sextant = max === r
    ? ((g - b) / delta) % 6
    : max === g
    ? (b - r) / delta + 2
    : (r - g) / delta + 4;

  return Math.round((sextant * 60 + 360) % 360);
}

// The hue of a metric, for the heatmap, which shades one hue by intensity
// rather than drawing a solid colour. Derived rather than stored so the grid
// and the line chart cannot drift apart: they are the same hue by construction.
//
// It reads the light-mode value in both themes on purpose. The heatmap's ramp
// runs from a pale, near-white base down towards saturated, which is a
// light-mode ramp; giving the grid a dark-mode ramp is a separate change.
export function metricHue(key: MetricKey): number {
  return hueOf(METRIC_COLORS[key].light);
}

// Every colour token for one mode, keyed by CSS custom property name.
export function themeTokens(mode: ThemeMode): Record<string, string> {
  const tokens: Record<string, string> = {};

  for (const key of METRIC_KEYS) {
    tokens[metricVar(key)] = METRIC_COLORS[key][mode];
  }
  for (const [name, value] of Object.entries(SEMANTIC_COLORS)) {
    tokens[`--${name}`] = value[mode];
  }
  for (const [name, value] of Object.entries(CHART_SURFACES)) {
    tokens[`--${name}`] = value[mode];
  }
  for (const [name, value] of Object.entries(SHADOWS)) {
    tokens[`--${name}`] = value[mode];
  }

  return tokens;
}

function block(selector: string, tokens: Record<string, string>, indent: string): string {
  const body = Object.entries(tokens)
    .map(([name, value]) => `${indent}  ${name}: ${value};`)
    .join('\n');
  return `${indent}${selector} {\n${body}\n${indent}}`;
}

// The stylesheet the application installs at startup. Light is the base, and
// dark applies both from the system preference (unless the user has pinned
// light) and from an explicit data-theme, which is the same three-selector
// shape App.css used before these values moved here.
//
// Emitting the tokens rather than checking a second copy of them into a
// stylesheet is what makes this file the only place a colour is written down.
// CSS still does the switching, so there is no flash and no work on the paint
// path; the one-off cost is a style element written before React mounts.
export function themeStylesheet(): string {
  const derived = Object.fromEntries(
    Object.entries(DERIVED_TOKENS).map(([name, value]) => [`--${name}`, value]),
  );
  const light = { ...themeTokens('light'), ...derived };
  const dark = themeTokens('dark');

  return [
    block(':root', light, ''),
    `@media (prefers-color-scheme: dark) {\n${block(':root:not([data-theme="light"])', dark, '  ')}\n}`,
    block(':root[data-theme="dark"]', dark, ''),
  ].join('\n\n') + '\n';
}
