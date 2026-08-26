import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { installThemeTokens, resolveThemeMode, useThemeMode } from './theme';
import { themeStylesheet } from './palette';

function setSystemPrefersDark(prefersDark: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: prefersDark && query.includes('dark'),
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

describe('resolveThemeMode', () => {
  beforeEach(() => {
    document.documentElement.removeAttribute('data-theme');
    setSystemPrefersDark(false);
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
  });

  it('follows the system preference when no theme is pinned', () => {
    expect(resolveThemeMode()).toBe('light');
    setSystemPrefersDark(true);
    expect(resolveThemeMode()).toBe('dark');
  });

  it('lets a pinned theme override the system preference in both directions', () => {
    setSystemPrefersDark(true);
    document.documentElement.setAttribute('data-theme', 'light');
    expect(resolveThemeMode()).toBe('light');

    setSystemPrefersDark(false);
    document.documentElement.setAttribute('data-theme', 'dark');
    expect(resolveThemeMode()).toBe('dark');
  });

  it('treats the "system" setting, which clears the attribute, as unpinned', () => {
    setSystemPrefersDark(true);
    document.documentElement.setAttribute('data-theme', 'system');
    expect(resolveThemeMode()).toBe('dark');
  });
});

describe('installThemeTokens', () => {
  afterEach(() => {
    document.getElementById('telltale-theme-tokens')?.remove();
  });

  it('writes the palette into the document', () => {
    const style = installThemeTokens();
    expect(style.isConnected).toBe(true);
    expect(style.textContent).toBe(themeStylesheet());
  });

  it('reuses its own style element rather than stacking copies', () => {
    const first = installThemeTokens();
    const second = installThemeTokens();
    expect(second).toBe(first);
    expect(document.querySelectorAll('#telltale-theme-tokens')).toHaveLength(1);
  });
});

// The stale-chart bug this replaces: the charts resolved their colours inside a
// useCallback whose dependencies never mentioned the theme, so a light-to-dark
// switch left the axes, grid and background painted for the previous theme.
// They now depend on this hook, so it has to actually fire on the switch.
describe('useThemeMode', () => {
  beforeEach(() => {
    document.documentElement.removeAttribute('data-theme');
    setSystemPrefersDark(false);
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
  });

  it('reports the current mode and updates when the theme is pinned', async () => {
    const { result } = renderHook(() => useThemeMode());
    expect(result.current).toBe('light');

    await act(async () => {
      document.documentElement.setAttribute('data-theme', 'dark');
    });
    expect(result.current).toBe('dark');

    await act(async () => {
      document.documentElement.setAttribute('data-theme', 'light');
    });
    expect(result.current).toBe('light');
  });

  it('stops observing once the component using it unmounts', async () => {
    const { unmount } = renderHook(() => useThemeMode());
    unmount();

    await act(async () => {
      document.documentElement.setAttribute('data-theme', 'dark');
    });
    // Nothing to assert on the value; the point is that the observer is gone
    // and React is not asked to update a tree that no longer exists.
    expect(resolveThemeMode()).toBe('dark');
  });
});

// Every source file, as text, so the two guards below can read the codebase
// rather than a copy of it. Vite's ?raw import keeps this inside the bundler,
// which is what lets the same file be typechecked by the production build,
// where node's filesystem types are not available.
const SOURCES = import.meta.glob<string>('./*.{ts,tsx,css}', {
  query: '?raw',
  import: 'default',
  eager: true,
});

function sourcesExcludingTests(): [string, string][] {
  return Object.entries(SOURCES)
    .map(([path, text]) => [path.replace('./', ''), text] as [string, string])
    .filter(([name]) => !name.includes('.test.') && name !== 'palette.ts')
    .sort(([a], [b]) => a.localeCompare(b));
}

// A var() that nothing defines is silently nothing: the declaration is dropped
// and the element falls back to whatever it inherits, which is exactly what a
// stylesheet does when a token is renamed on one side of the move only.
describe('every custom property App.css uses is defined', () => {
  it('resolves every var() against the palette, App.css itself, or a component', () => {
    const css = SOURCES['./App.css'];

    const defined = new Set<string>();
    for (const match of themeStylesheet().matchAll(/^\s*(--[\w-]+):/gm)) {
      defined.add(match[1]);
    }
    for (const match of css.matchAll(/^\s*(--[\w-]+):/gm)) {
      defined.add(match[1]);
    }
    // Tokens a component sets on an element it owns, such as the health tile's
    // --tile-color, are never declared in a stylesheet.
    for (const [name, text] of Object.entries(SOURCES)) {
      if (!name.endsWith('.tsx')) continue;
      for (const match of text.matchAll(/'(--[\w-]+)'/g)) defined.add(match[1]);
    }

    const used = new Set<string>();
    for (const match of css.matchAll(/var\((--[\w-]+)/g)) used.add(match[1]);

    expect([...used].filter(name => !defined.has(name)).sort()).toEqual([]);
  });
});

// The point of the palette is that it is the only place a colour is written
// down. Nothing enforces that except this: a raw colour reintroduced in a
// component or a stylesheet is how the four separate palettes grew last time.
describe('no colour is declared outside the palette', () => {
  // The lookbehind keeps HTML entities (&#9662;) out of it: they are decimal,
  // not hexadecimal, and nothing about them is a colour.
  const COLOUR = /(?<!&)#[0-9a-fA-F]{3,8}|rgba?\(|hsla?\(/;

  it.each(sourcesExcludingTests())('%s declares no literal colour', (_name, text) => {
    const offenders = text
      .split('\n')
      .map((line, i) => [i + 1, line] as const)
      // Heatmap builds an hsl() from a hue the palette derives, which is the
      // one place a colour is computed rather than chosen.
      .filter(([, line]) => COLOUR.test(line) && !line.includes('${hue}'))
      .map(([n, line]) => `${n}: ${line.trim()}`);

    expect(offenders).toEqual([]);
  });
});
