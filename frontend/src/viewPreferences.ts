/**
 * Remembers the shape of the view between windows.
 *
 * Telltale opens its window on `/?s=<token>` and nothing else, so the URL, which
 * carries the view within a session, has nothing to say at startup. Without this
 * the window always comes back on today at Day scale, Auto granularity, the
 * Overview tab, sorted by CPU, however the last one was left.
 *
 * What is remembered is the shape of the view, not the position in it. The scale
 * comes back; the date does not. A window reopening on a day two months old
 * reads as a recorder that has stopped recording, and the point of opening it is
 * almost always to see what the machine is doing now.
 *
 * Three other pieces of view state are deliberately left out. The process filter
 * text, because it is the one part of the view that records what someone went
 * looking for, and this tool treats what a person runs on their own machine as
 * private by default. The custom range and the hour selection, because both are
 * offsets into one specific day and the day is not being restored. And the
 * drill-down, because that is a position in browser history rather than a
 * preference.
 *
 * Everything here stays on the machine, in the same place and by the same means
 * as the existing theme setting. Nothing is sent anywhere or written to the
 * capture database.
 */

import { GRANULARITIES } from './granularity';
import type { GranularityId } from './granularity';
import type { DashboardTab, ProcessSort, ViewScale, ViewState } from './types';
import type { ProcessCategory } from './utils';

/**
 * The version is in the key rather than in the value. A build that changes the
 * shape of what is stored writes under a new key, so it can never read an older
 * build's object as though it were its own, and the old key simply goes unread.
 */
export const VIEW_PREFERENCES_KEY = 'telltale-view-v1';

export interface ViewPreferences {
  scale: ViewScale;
  granularity: GranularityId;
  /**
   * The scale the granularity was chosen under. Stored because a bucket width
   * only means something against a span: five seconds is a reasonable division
   * of a day and a meaningless one of a year.
   */
  granularityScale: ViewScale;
  tab: DashboardTab;
  /** True for the heatmap, false for the chart. Only offered above Day scale. */
  heatmap: boolean;
  sort: ProcessSort;
  category: ProcessCategory | 'all';
}

/** What the window opens on when nothing has been saved, which is today's behaviour. */
export const DEFAULT_VIEW_PREFERENCES: ViewPreferences = {
  scale: 'day',
  granularity: 'auto',
  granularityScale: 'day',
  tab: 'overview',
  heatmap: false,
  sort: 'cpu',
  category: 'all',
};

// Each union is validated through a lookup keyed by the union itself, so adding
// a member to any of them fails the build here until this table is updated. A
// plain array of strings would have gone quietly out of date instead, and a
// value the rest of the app cannot render would have made it back out of
// storage.
const VIEW_SCALES: Record<ViewScale, true> = {
  year: true, month: true, week: true, day: true,
};

const DASHBOARD_TABS: Record<DashboardTab, true> = {
  overview: true, alerts: true, processes: true,
};

const PROCESS_SORTS: Record<ProcessSort, true> = {
  cpu: true, memory: true, io: true, name: true,
};

const CATEGORY_FILTERS: Record<ProcessCategory | 'all', true> = {
  all: true, system: true, services: true, applications: true,
};

/**
 * Membership of one of the tables above.
 *
 * `hasOwnProperty` rather than `in`, because `in` also finds everything on
 * `Object.prototype`, and a stored value of "constructor" or "toString" would
 * pass as a valid scale.
 */
function isMemberOf(table: object, value: unknown): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(table, value);
}

export function isViewScale(value: unknown): value is ViewScale {
  return isMemberOf(VIEW_SCALES, value);
}

function isDashboardTab(value: unknown): value is DashboardTab {
  return isMemberOf(DASHBOARD_TABS, value);
}

function isProcessSort(value: unknown): value is ProcessSort {
  return isMemberOf(PROCESS_SORTS, value);
}

function isCategoryFilter(value: unknown): value is ProcessCategory | 'all' {
  return isMemberOf(CATEGORY_FILTERS, value);
}

/** Checked against the option list itself, which is the one place they are defined. */
function isGranularityId(value: unknown): value is GranularityId {
  return typeof value === 'string' && GRANULARITIES.some(option => option.id === value);
}

/**
 * The store to use, or null where there is none.
 *
 * Reading `localStorage` is itself capable of throwing, not just reading from
 * it: a browser configured to block site data refuses at the property. Every
 * path through this module treats that as "nothing was saved", which is exactly
 * how the window behaved before any of this existed.
 */
function defaultStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * What the last window was left on, with anything unrecognised replaced by its
 * default. Never throws and never returns a partial object.
 */
export function loadViewPreferences(storage: Storage | null = defaultStorage()): ViewPreferences {
  const saved = readObject(storage);
  if (saved === null) return { ...DEFAULT_VIEW_PREFERENCES };

  const savedScale = isViewScale(saved.scale) ? saved.scale : null;
  const scale = savedScale ?? DEFAULT_VIEW_PREFERENCES.scale;

  // An entry that does not say which scale its granularity was chosen under
  // cannot be trusted to pair the two, so the granularity is dropped rather than
  // applied to whichever scale happens to be restored.
  //
  // An entry whose own scale did not survive validation is treated the same way,
  // because the granularity would then be paired against a scale that was
  // substituted rather than saved. A width the picker would never have offered
  // for the restored span could otherwise come back looking like a choice.
  const pairedScale = savedScale !== null && isViewScale(saved.granularityScale)
    ? saved.granularityScale
    : null;
  const granularity = pairedScale !== null && isGranularityId(saved.granularity)
    ? saved.granularity
    : DEFAULT_VIEW_PREFERENCES.granularity;

  return {
    scale,
    granularity,
    granularityScale: pairedScale ?? scale,
    tab: isDashboardTab(saved.tab) ? saved.tab : DEFAULT_VIEW_PREFERENCES.tab,
    heatmap: typeof saved.heatmap === 'boolean' ? saved.heatmap : DEFAULT_VIEW_PREFERENCES.heatmap,
    sort: isProcessSort(saved.sort) ? saved.sort : DEFAULT_VIEW_PREFERENCES.sort,
    category: isCategoryFilter(saved.category) ? saved.category : DEFAULT_VIEW_PREFERENCES.category,
  };
}

/** The stored object, or null when there is nothing readable there. */
function readObject(storage: Storage | null): Record<string, unknown> | null {
  if (storage === null) return null;

  let raw: string | null;
  try {
    raw = storage.getItem(VIEW_PREFERENCES_KEY);
  } catch {
    return null;
  }
  if (raw === null) return null;

  try {
    const parsed: unknown = JSON.parse(raw);
    // Arrays are objects too, and would read as an object with no keys, so every
    // field would silently take its default rather than the entry being rejected.
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null;
    return parsed as Record<string, unknown>;
  } catch {
    return null;
  }
}

/**
 * Writes the view for the next window to open on.
 *
 * A failure is not reported. Persisting a preference is not what the window is
 * for, and a browser that refuses to store it leaves someone with the behaviour
 * they already had rather than an error about a setting they never asked to
 * save.
 */
export function saveViewPreferences(
  preferences: ViewPreferences,
  storage: Storage | null = defaultStorage(),
): void {
  if (storage === null) return;

  try {
    storage.setItem(VIEW_PREFERENCES_KEY, JSON.stringify(preferences));
  } catch {
    // Blocked site data, or a full store. Neither is worth interrupting anyone over.
  }
}

/**
 * The saved granularity, but only where it still applies.
 *
 * `navigate` drops back to Auto whenever the scale changes, on the grounds that
 * a width chosen for one span means nothing against another. Restoring has to
 * follow the same rule, or a stale entry would reintroduce exactly the pairing
 * the app takes care to avoid.
 */
export function restoredGranularity(preferences: ViewPreferences): GranularityId {
  return preferences.granularityScale === preferences.scale
    ? preferences.granularity
    : 'auto';
}

/**
 * The current period at a given scale.
 *
 * Built in the same shape TimeNav's own scale buttons produce, so a restored
 * view is indistinguishable from one reached by pressing them: no month above
 * Month scale, and a day only where a day means something.
 */
export function viewForScale(scale: ViewScale, now: Date): ViewState {
  const view: ViewState = { scale, year: now.getFullYear() };
  if (scale !== 'year') view.month = now.getMonth() + 1;
  if (scale === 'day' || scale === 'week') view.day = now.getDate();
  return view;
}
