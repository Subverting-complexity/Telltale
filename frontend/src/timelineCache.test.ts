import { describe, it, expect } from 'vitest';
import { TimelineCache } from './timelineCache';
import type { TimelineResponse } from './types';

/** A response distinguishable from another by its resolution, which nothing here reads for meaning. */
function answer(resolution: string, bucketRequestMs: number | null = null): TimelineResponse {
  return {
    resolution,
    bucketMs: bucketRequestMs ?? 0,
    bucketRequestMs,
    minBucketMs: 0,
    tierFloorMs: 5_000,
    points: [],
  };
}

describe('TimelineCache', () => {
  it('has nothing for a window and width it has not been given', () => {
    const cache = new TimelineCache();

    expect(cache.get(0, 100, null)).toBeUndefined();
  });

  it('hands back the answer it was given for the same window and width', () => {
    const cache = new TimelineCache();
    const served = answer('machine', 60_000);

    cache.set(0, 100, 60_000, served);

    expect(cache.get(0, 100, 60_000)).toBe(served);
  });

  it('does not answer for a different window', () => {
    const cache = new TimelineCache();
    cache.set(0, 100, 60_000, answer('machine', 60_000));

    // The caller clears on a window change, so this should never be asked. The
    // key carries the window anyway, so a clear that was missed shows up as a
    // miss rather than as yesterday's chart drawn under today's date.
    expect(cache.get(0, 200, 60_000)).toBeUndefined();
    expect(cache.get(50, 100, 60_000)).toBeUndefined();
  });

  it('tells Auto apart from a named width', () => {
    const cache = new TimelineCache();
    const auto = answer('auto-answer', null);
    const minute = answer('minute-answer', 60_000);

    cache.set(0, 100, null, auto);
    cache.set(0, 100, 60_000, minute);

    expect(cache.get(0, 100, null)).toBe(auto);
    expect(cache.get(0, 100, 60_000)).toBe(minute);
  });

  it('keys on the width asked for, not the width served', () => {
    const cache = new TimelineCache();

    // Both were widened by the server to a whole minute. They are still
    // different questions: the 5 second request has a notice to show explaining
    // the widening, and the 1 minute request has none. Keying on what came back
    // would answer the first from the second and lose the notice.
    const widened: TimelineResponse = { ...answer('machine', 5_000), bucketMs: 60_000 };
    cache.set(0, 100, 5_000, widened);

    expect(cache.get(0, 100, 60_000)).toBeUndefined();
    expect(cache.get(0, 100, 5_000)).toBe(widened);
  });

  it('is empty again after a clear', () => {
    const cache = new TimelineCache();
    cache.set(0, 100, null, answer('machine'));
    cache.set(0, 100, 60_000, answer('machine', 60_000));
    expect(cache.size).toBe(2);

    cache.clear();

    expect(cache.size).toBe(0);
    expect(cache.get(0, 100, null)).toBeUndefined();
  });

  it('replaces an answer rather than accumulating one per fetch', () => {
    const cache = new TimelineCache();
    const first = answer('first');
    const second = answer('second');

    cache.set(0, 100, null, first);
    cache.set(0, 100, null, second);

    expect(cache.size).toBe(1);
    expect(cache.get(0, 100, null)).toBe(second);
  });
});
