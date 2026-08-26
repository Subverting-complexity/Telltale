import { useSyncExternalStore } from 'react';
import type { ThemeMode } from './palette';
import { themeStylesheet } from './palette';

// Resolving the active theme, installing the palette's custom properties, and
// telling React when either changes. palette.ts decides what the colours are;
// this file decides when they apply.

const STYLE_ELEMENT_ID = 'telltale-theme-tokens';
const DARK_QUERY = '(prefers-color-scheme: dark)';

// Mirrors the CSS: an explicit data-theme wins, and with none set the system
// preference decides. Anything other than "dark" or "light" on the attribute is
// treated as unset, which is how the "system" setting is stored.
export function resolveThemeMode(doc: Document = document): ThemeMode {
  const attribute = doc.documentElement.getAttribute('data-theme');
  if (attribute === 'dark') return 'dark';
  if (attribute === 'light') return 'light';
  return matchesDark() ? 'dark' : 'light';
}

function matchesDark(): boolean {
  return typeof window !== 'undefined'
    && typeof window.matchMedia === 'function'
    && window.matchMedia(DARK_QUERY).matches;
}

// Writes the palette into the document as CSS custom properties. Called once,
// from main.tsx, before React mounts, so the first paint already has them.
// Idempotent, because a hot reload in development re-runs the module.
export function installThemeTokens(doc: Document = document): HTMLStyleElement {
  const existing = doc.getElementById(STYLE_ELEMENT_ID);
  const style = existing instanceof HTMLStyleElement
    ? existing
    : doc.createElement('style');

  style.id = STYLE_ELEMENT_ID;
  style.textContent = themeStylesheet();
  if (!style.isConnected) doc.head.appendChild(style);

  return style;
}

// Fires on both ways the mode can change: the theme button setting data-theme,
// and the operating system flipping its own preference while the window is open.
function subscribeToThemeMode(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);
  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['data-theme'],
  });

  const media = typeof window.matchMedia === 'function'
    ? window.matchMedia(DARK_QUERY)
    : null;
  media?.addEventListener('change', onChange);

  return () => {
    observer.disconnect();
    media?.removeEventListener('change', onChange);
  };
}

// The active mode, as a value a component can depend on.
//
// The uPlot charts need this. They paint onto a canvas, so a CSS variable
// cannot reach them and their colours have to be resolved when the chart is
// built. Before this hook, that resolution happened inside a useCallback whose
// dependencies did not mention the theme, so switching light to dark left the
// axes, grid and background painted for the previous theme until the chart
// rebuilt for some unrelated reason.
export function useThemeMode(): ThemeMode {
  return useSyncExternalStore(
    subscribeToThemeMode,
    () => resolveThemeMode(),
    () => 'light' as ThemeMode,
  );
}
