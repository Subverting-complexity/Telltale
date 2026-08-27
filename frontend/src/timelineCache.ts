import type { TimelineResponse } from './types';

/**
 * Timeline answers already fetched, so returning to a granularity costs nothing.
 *
 * The detail picker invites exactly the pattern that is most expensive without
 * this: comparing two granularities means alternating between them, and every
 * alternation used to re-run the same query over the same window against a
 * database that had not changed in between.
 *
 * Everything here is in memory and lives as long as the cache object does.
 * Nothing is written to `localStorage`, `sessionStorage`, or anywhere on disk.
 * Telltale records what someone runs on their own machine, and a recording
 * should not leave a copy of itself behind in the browser's storage.
 *
 * Freshness is handled by throwing the whole cache away rather than by ageing
 * entries. The two things that can make an entry wrong are the window moving and
 * the recording growing, and the caller already knows when both happen: it
 * clears on a change of window, on the manual refresh, and on the ninety second
 * one. An entry can therefore never be older than the last refresh, which is the
 * same guarantee the uncached screen gave.
 */
export class TimelineCache {
  private readonly entries = new Map<string, TimelineResponse>();

  /**
   * The answer already held for this window and requested width, or `undefined`.
   *
   * `bucketMs` is what was *asked* for, not what came back. Two requests that the
   * server widened to the same width are still different questions, and the
   * response carries the difference in `bucketRequestMs`, which is what the
   * clamp notice reads. Keying on the served width would let a 5 second request
   * be answered from a 1 minute one and lose the notice explaining the widening.
   */
  get(from: number, to: number, bucketMs: number | null): TimelineResponse | undefined {
    return this.entries.get(key(from, to, bucketMs));
  }

  set(from: number, to: number, bucketMs: number | null, response: TimelineResponse): void {
    this.entries.set(key(from, to, bucketMs), response);
  }

  clear(): void {
    this.entries.clear();
  }

  /** How many answers are held. For tests; nothing in the app reads it. */
  get size(): number {
    return this.entries.size;
  }
}

/**
 * The window is part of the key as well as the width, even though the cache is
 * cleared whenever the window changes. The clear is what keeps entries fresh;
 * the key is what makes serving an answer for the wrong window impossible rather
 * than merely unlikely, so a missed clear shows up as a cache miss instead of as
 * a chart drawn from the previous day.
 *
 * There is no eviction. Widths come from the fixed list of granularity options,
 * so one window can hold at most that many entries, and the window changing
 * empties the cache anyway.
 */
function key(from: number, to: number, bucketMs: number | null): string {
  return `${from}:${to}:${bucketMs ?? 'auto'}`;
}
