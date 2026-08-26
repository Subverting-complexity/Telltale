/**
 * How finely a span of time is divided on the timeline. `bucketMs` is the width
 * asked of the server; `null` means let the server decide from the span, which
 * is what it has always done.
 */
export type GranularityId = 'auto' | '5s' | '1m' | '10m' | '1h' | '1d';

export interface GranularityOption {
  id: GranularityId;
  label: string;
  /** Short form for the notice line, e.g. "10 minute". */
  detail: string;
  bucketMs: number | null;
}

export const GRANULARITIES: GranularityOption[] = [
  { id: 'auto', label: 'Auto', detail: 'automatic', bucketMs: null },
  { id: '5s', label: '5 sec', detail: '5 second', bucketMs: 5_000 },
  { id: '1m', label: '1 min', detail: '1 minute', bucketMs: 60_000 },
  { id: '10m', label: '10 min', detail: '10 minute', bucketMs: 600_000 },
  { id: '1h', label: '1 hour', detail: '1 hour', bucketMs: 3_600_000 },
  { id: '1d', label: '1 day', detail: '1 day', bucketMs: 86_400_000 },
];

/**
 * The most points one timeline response carries. This is the viewer's own
 * `TierSelection.MaxRawOnlyPoints`, repeated here because the two sides of the
 * HTTP boundary share no code.
 *
 * The server stays authoritative. This copy only decides which buttons look
 * available, so if the two ever drift, the worst that happens is a button that
 * should have been greyed out gets clicked and the server widens the request,
 * which the notice below then explains.
 */
export const MAX_POINTS = 20_000;

export function granularityById(id: GranularityId): GranularityOption {
  return GRANULARITIES.find(g => g.id === id) ?? GRANULARITIES[0];
}

export interface GranularityAvailability {
  available: boolean;
  /** Why not, for the button's tooltip. Empty when it is available. */
  reason: string;
}

/**
 * Whether an option can be served for the window now on screen, and if not, why.
 *
 * Two things rule one out, and they are worth telling apart because only one of
 * them is about the recording rather than the request. `minBucketMs` is the
 * finest the server said this window could be served at; anything above the
 * point cap it demands is a retention limit, and the recording simply no longer
 * holds that detail.
 *
 * `minBucketMs` is `null` while the first response is still in flight, which
 * leaves every option available rather than greying the control out and back in.
 */
export function granularityAvailability(
  option: GranularityOption,
  rangeMs: number,
  minBucketMs: number | null,
): GranularityAvailability {
  if (option.bucketMs === null) return { available: true, reason: '' };

  const capFloorMs = rangeMs > 0 ? Math.ceil(rangeMs / MAX_POINTS) : 0;
  if (option.bucketMs < capFloorMs) {
    return { available: false, reason: 'More points than one response carries for this span.' };
  }

  if (minBucketMs !== null && option.bucketMs < minBucketMs) {
    return { available: false, reason: 'Finer detail is not retained this far back.' };
  }

  return { available: true, reason: '' };
}

/**
 * One line explaining a request the server widened, or `null` when it served
 * what was asked for.
 *
 * A response with no bucket at all is never a widening: it means every recorded
 * sample came back, which is as fine as the timeline goes.
 */
export function clampNotice(
  served: { bucketMs: number; bucketRequestMs: number | null },
  rangeMs: number,
): string | null {
  const { bucketMs, bucketRequestMs } = served;
  if (bucketRequestMs === null || bucketMs <= 0 || bucketMs <= bucketRequestMs) return null;

  // The server rounds its own cap floor up to a whole tier interval, so this is
  // a lower bound on it. A request landing between the two is attributed to
  // retention when the cap was really what moved it; both are true of a request
  // that narrow, and the sentence reads the same either way.
  const capFloorMs = rangeMs > 0 ? Math.ceil(rangeMs / MAX_POINTS) : 0;
  const because = bucketRequestMs < capFloorMs
    ? 'that would be more points than one response carries'
    : 'finer detail is not retained this far back';

  return `Showing ${describeBucket(bucketMs)} detail. You asked for `
    + `${describeBucket(bucketRequestMs)}, but ${because}.`;
}

/**
 * A bucket width in words.
 *
 * The server can answer with a width that is none of the offered options,
 * because it rounds a request up to whatever the point cap and the tier
 * intervals between them demand. Those land on arbitrary numbers, so anything
 * unnamed is described at the largest unit it exceeds and rounded to one
 * decimal, which reads better in a sentence than 1,580 seconds would.
 */
export function describeBucket(bucketMs: number): string {
  const named = GRANULARITIES.find(g => g.bucketMs === bucketMs);
  if (named) return named.detail;

  if (bucketMs >= 3_600_000) return `${trim(bucketMs / 3_600_000)} hour`;
  if (bucketMs >= 60_000) return `${trim(bucketMs / 60_000)} minute`;
  if (bucketMs >= 1_000) return `${trim(bucketMs / 1_000)} second`;
  return `${bucketMs}ms`;
}

function trim(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}
