/**
 * How finely a span of time is divided on the timeline. `bucketMs` is the width
 * asked of the server; `null` means let the server decide from the span, which
 * is what it has always done.
 */
export type GranularityId = 'auto' | '5s' | '1m' | '10m' | '1h' | '1d';

export interface GranularityOption {
  id: GranularityId;
  label: string;
  bucketMs: number | null;
}

export const GRANULARITIES: GranularityOption[] = [
  { id: 'auto', label: 'Auto', bucketMs: null },
  { id: '5s', label: '5 sec', bucketMs: 5_000 },
  { id: '1m', label: '1 min', bucketMs: 60_000 },
  { id: '10m', label: '10 min', bucketMs: 600_000 },
  { id: '1h', label: '1 hour', bucketMs: 3_600_000 },
  { id: '1d', label: '1 day', bucketMs: 86_400_000 },
];

/**
 * What the last response said about how it was served. The two floors are what
 * make it possible to say which granularities are worth offering, and why one
 * is not.
 */
export interface TimelineDetail {
  bucketMs: number;
  bucketRequestMs: number | null;
  /** Finest width this window can be served at, whichever limit binds. 0 means none does. */
  minBucketMs: number;
  /** Finest width the tiers themselves store, ignoring how many points that comes to. */
  tierFloorMs: number;
}

/**
 * The fewest points worth drawing a line through. A bucket that divides the
 * window into one or two points produces a chart with nothing to read.
 */
const MIN_USEFUL_POINTS = 3;

export function granularityById(id: GranularityId): GranularityOption {
  return GRANULARITIES.find(g => g.id === id) ?? GRANULARITIES[0];
}

export interface GranularityAvailability {
  available: boolean;
  /** Why not, for the button. Empty when it is available. */
  reason: string;
}

/**
 * Whether an option is worth offering for the window now on screen, and if not,
 * why.
 *
 * What the server can serve is not worked out here. It cannot be: the server
 * measures against the span it actually reads, which is the requested window
 * clamped to what the tiers hold, and the two differ whenever the recording is
 * shorter than the period on screen. Since the Month and Year views run to the
 * end of the period, that is most of the time, and a local estimate would grey
 * out options the server would have served without a murmur. `served.minBucketMs`
 * is the server's own answer over its own span, so it is used directly.
 *
 * `served` is null until the first response arrives, which leaves every option
 * available rather than greying the control out and back in. Being briefly too
 * permissive is the harmless direction: the request goes out, the server widens
 * it if it must, and the notice explains what happened.
 *
 * The one rule decided locally is the last one, and it is about legibility
 * rather than capability, so it can never withhold something the server would
 * have served usefully.
 */
export function granularityAvailability(
  option: GranularityOption,
  rangeMs: number,
  served: TimelineDetail | null,
): GranularityAvailability {
  if (option.bucketMs === null) return { available: true, reason: '' };

  if (rangeMs > 0 && option.bucketMs * MIN_USEFUL_POINTS > rangeMs) {
    return { available: false, reason: 'Too wide for the span on screen.' };
  }

  if (served !== null && option.bucketMs < served.minBucketMs) {
    return { available: false, reason: reasonFor(option.bucketMs, served) };
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
export function clampNotice(served: TimelineDetail): string | null {
  const { bucketMs, bucketRequestMs } = served;
  if (bucketRequestMs === null || bucketMs <= 0 || bucketMs <= bucketRequestMs) return null;

  return `Showing ${describeBucket(bucketMs)} detail. You asked for `
    + `${describeBucket(bucketRequestMs)} detail, but ${reasonFor(bucketRequestMs, served).toLowerCase()}`;
}

/**
 * Which of the two limits refused a width. Below the tier floor the recording no
 * longer holds that detail at all; above it, the window is merely too wide to
 * return that many points of it.
 */
function reasonFor(bucketMs: number, served: TimelineDetail): string {
  return bucketMs < served.tierFloorMs
    ? 'Finer detail is not retained this far back.'
    : 'That would be more points than one response carries.';
}

/**
 * A bucket width in words.
 *
 * The server can answer with a width that is none of the offered options,
 * because it rounds a request up to whatever the point cap and the tier
 * intervals between them demand. Those land on arbitrary numbers, so a width is
 * described at the largest unit it exceeds and rounded to one decimal, which
 * reads better in a sentence than 1,580 seconds would.
 */
export function describeBucket(bucketMs: number): string {
  if (bucketMs >= 86_400_000) return `${trim(bucketMs / 86_400_000)} day`;
  if (bucketMs >= 3_600_000) return `${trim(bucketMs / 3_600_000)} hour`;
  if (bucketMs >= 60_000) return `${trim(bucketMs / 60_000)} minute`;
  if (bucketMs >= 1_000) return `${trim(bucketMs / 1_000)} second`;
  return `${bucketMs}ms`;
}

function trim(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}
