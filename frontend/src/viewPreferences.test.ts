import { describe, it, expect, beforeEach } from 'vitest';
import {
  DEFAULT_VIEW_PREFERENCES,
  VIEW_PREFERENCES_KEY,
  loadViewPreferences,
  restoredGranularity,
  saveViewPreferences,
  viewForScale,
} from './viewPreferences';
import type { ViewPreferences } from './viewPreferences';

/** A saved entry that differs from the defaults in every field. */
const SAVED: ViewPreferences = {
  scale: 'week',
  granularity: '10m',
  granularityScale: 'week',
  tab: 'processes',
  heatmap: true,
  sort: 'memory',
  category: 'applications',
};

/** Puts a value under the key without going through `saveViewPreferences`. */
function write(value: unknown) {
  localStorage.setItem(VIEW_PREFERENCES_KEY, JSON.stringify(value));
}

/** A store that refuses everything, as one does when site data is blocked. */
function blockedStorage(): Storage {
  const refuse = () => { throw new DOMException('The operation is insecure.'); };
  return {
    get length(): number { return refuse(); },
    clear: refuse,
    getItem: refuse,
    key: refuse,
    removeItem: refuse,
    setItem: refuse,
  } as unknown as Storage;
}

beforeEach(() => {
  localStorage.clear();
});

describe('loadViewPreferences', () => {
  it('returns the defaults when nothing has been saved', () => {
    expect(loadViewPreferences()).toEqual(DEFAULT_VIEW_PREFERENCES);
  });

  it('returns what was saved', () => {
    saveViewPreferences(SAVED);
    expect(loadViewPreferences()).toEqual(SAVED);
  });

  it('replaces an unrecognised value with its default and keeps the rest', () => {
    write({ ...SAVED, scale: 'fortnight', tab: 'nonsense' });

    const loaded = loadViewPreferences();

    expect(loaded.scale).toBe(DEFAULT_VIEW_PREFERENCES.scale);
    expect(loaded.tab).toBe(DEFAULT_VIEW_PREFERENCES.tab);
    expect(loaded.sort).toBe(SAVED.sort);
    expect(loaded.category).toBe(SAVED.category);
  });

  it('rejects a value inherited from Object.prototype', () => {
    // "toString" is on every object, so a membership test written with `in`
    // rather than hasOwnProperty would let it through as a scale.
    write({ ...SAVED, scale: 'toString', sort: 'constructor' });

    const loaded = loadViewPreferences();

    expect(loaded.scale).toBe(DEFAULT_VIEW_PREFERENCES.scale);
    expect(loaded.sort).toBe(DEFAULT_VIEW_PREFERENCES.sort);
  });

  it('ignores a heatmap flag that is not a boolean', () => {
    write({ ...SAVED, heatmap: 'yes' });
    expect(loadViewPreferences().heatmap).toBe(DEFAULT_VIEW_PREFERENCES.heatmap);
  });

  it('drops the granularity when the entry does not say which scale it belonged to', () => {
    write({ ...SAVED, granularityScale: undefined });

    const loaded = loadViewPreferences();

    expect(loaded.granularity).toBe('auto');
    expect(loaded.granularityScale).toBe(loaded.scale);
  });

  it('keeps a granularity paired with a different scale, for the caller to reject', () => {
    // Loading reports what was stored. Whether it still applies is
    // `restoredGranularity`'s decision, and it needs both values to make it.
    write({ ...SAVED, scale: 'day', granularityScale: 'year' });

    const loaded = loadViewPreferences();

    expect(loaded.granularity).toBe(SAVED.granularity);
    expect(loaded.granularityScale).toBe('year');
  });

  it('falls back to the defaults on a payload that is not an object', () => {
    write('week');
    expect(loadViewPreferences()).toEqual(DEFAULT_VIEW_PREFERENCES);
  });

  it('falls back to the defaults on an array, which is an object but has no fields', () => {
    write(['week']);
    expect(loadViewPreferences()).toEqual(DEFAULT_VIEW_PREFERENCES);
  });

  it('falls back to the defaults on a payload that is not JSON', () => {
    localStorage.setItem(VIEW_PREFERENCES_KEY, '{ not json');
    expect(loadViewPreferences()).toEqual(DEFAULT_VIEW_PREFERENCES);
  });

  it('returns the defaults rather than throwing when the store refuses to be read', () => {
    expect(loadViewPreferences(blockedStorage())).toEqual(DEFAULT_VIEW_PREFERENCES);
  });

  it('returns the defaults when there is no store at all', () => {
    expect(loadViewPreferences(null)).toEqual(DEFAULT_VIEW_PREFERENCES);
  });
});

describe('saveViewPreferences', () => {
  it('writes under a versioned key', () => {
    saveViewPreferences(SAVED);
    expect(localStorage.getItem(VIEW_PREFERENCES_KEY)).toBe(JSON.stringify(SAVED));
  });

  it('does not throw when the store refuses to be written to', () => {
    expect(() => saveViewPreferences(SAVED, blockedStorage())).not.toThrow();
  });

  it('does nothing when there is no store at all', () => {
    expect(() => saveViewPreferences(SAVED, null)).not.toThrow();
  });
});

describe('restoredGranularity', () => {
  it('keeps a granularity chosen under the scale being restored', () => {
    expect(restoredGranularity(SAVED)).toBe('10m');
  });

  it('falls back to Auto when the granularity belonged to another scale', () => {
    // The same rule `navigate` applies: a width chosen for one span means
    // nothing against another.
    expect(restoredGranularity({ ...SAVED, scale: 'year' })).toBe('auto');
  });
});

describe('viewForScale', () => {
  const now = new Date(2026, 7, 26);

  it('gives a day view the full date', () => {
    expect(viewForScale('day', now)).toEqual({ scale: 'day', year: 2026, month: 8, day: 26 });
  });

  it('gives a week view a day to anchor on', () => {
    expect(viewForScale('week', now)).toEqual({ scale: 'week', year: 2026, month: 8, day: 26 });
  });

  it('leaves the day off a month view', () => {
    expect(viewForScale('month', now)).toEqual({ scale: 'month', year: 2026, month: 8 });
  });

  it('leaves the month and the day off a year view', () => {
    expect(viewForScale('year', now)).toEqual({ scale: 'year', year: 2026 });
  });
});
