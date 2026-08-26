import { describe, it, expect } from 'vitest';
import {
  GRANULARITIES, granularityById, granularityAvailability, clampNotice, describeBucket, MAX_POINTS,
} from './granularity';

const DAY = 86_400_000;
const YEAR = 365 * DAY;

function option(id: string) {
  const found = GRANULARITIES.find(g => g.id === id);
  if (!found) throw new Error(`no such granularity: ${id}`);
  return found;
}

describe('granularityById', () => {
  it('returns the named option', () => {
    expect(granularityById('10m').bucketMs).toBe(600_000);
  });
  it('falls back to Auto for anything unrecognised', () => {
    expect(granularityById('nonsense' as never).id).toBe('auto');
  });
});

describe('granularityAvailability', () => {
  it('always offers Auto', () => {
    expect(granularityAvailability(option('auto'), YEAR, 600_000).available).toBe(true);
  });

  it('offers every option on a day served at full detail', () => {
    for (const g of GRANULARITIES) {
      expect(granularityAvailability(g, DAY, 0).available, g.id).toBe(true);
    }
  });

  it('withholds anything that would exceed the point cap for the span', () => {
    // A year at ten minute buckets is about 52,000 points, well past the cap.
    const tenMinutes = granularityAvailability(option('10m'), YEAR, 0);
    expect(tenMinutes.available).toBe(false);
    expect(tenMinutes.reason).toMatch(/points/);

    // An hour clears it, so the cap is the thing being tested rather than the
    // whole control being switched off.
    expect(granularityAvailability(option('1h'), YEAR, 0).available).toBe(true);
  });

  it('withholds anything finer than the recording still holds', () => {
    const fineness = 600_000;
    const fiveSeconds = granularityAvailability(option('5s'), DAY, fineness);
    expect(fiveSeconds.available).toBe(false);
    expect(fiveSeconds.reason).toMatch(/retained/);

    expect(granularityAvailability(option('10m'), DAY, fineness).available).toBe(true);
  });

  it('offers everything before the first response has arrived', () => {
    // minBucketMs is unknown until the server has answered once. Greying the
    // control out and back in on every navigation would be worse than briefly
    // offering an option the server then widens.
    for (const g of GRANULARITIES) {
      expect(granularityAvailability(g, DAY, null).available, g.id).toBe(true);
    }
  });

  it('treats an empty span as unconstrained by the cap', () => {
    expect(granularityAvailability(option('5s'), 0, 0).available).toBe(true);
  });

  it('keeps the cap threshold in step with the constant it is derived from', () => {
    // One point per bucket exactly at the cap is allowed; one finer is not.
    const span = MAX_POINTS * 60_000;
    expect(granularityAvailability(option('1m'), span, 0).available).toBe(true);
    expect(granularityAvailability(option('5s'), span, 0).available).toBe(false);
  });
});

describe('clampNotice', () => {
  it('says nothing when no granularity was asked for', () => {
    expect(clampNotice({ bucketMs: 600_000, bucketRequestMs: null }, DAY)).toBeNull();
  });

  it('says nothing when the request was served as asked', () => {
    expect(clampNotice({ bucketMs: 600_000, bucketRequestMs: 600_000 }, DAY)).toBeNull();
  });

  it('says nothing when every recorded sample came back', () => {
    // A bucket of zero is full detail, which is never less than what was asked
    // for however fine the request was.
    expect(clampNotice({ bucketMs: 0, bucketRequestMs: 5_000 }, DAY)).toBeNull();
  });

  it('blames retention when the span could have carried the request', () => {
    const notice = clampNotice({ bucketMs: 600_000, bucketRequestMs: 5_000 }, DAY);
    expect(notice).toBe(
      'Showing 10 minute detail. You asked for 5 second, but finer detail is not retained this far back.');
  });

  it('blames the point cap when the span could not have carried the request', () => {
    const notice = clampNotice({ bucketMs: 3_600_000, bucketRequestMs: 5_000 }, YEAR);
    expect(notice).toMatch(/more points than one response carries/);
    expect(notice).toMatch(/^Showing 1 hour detail\./);
  });
});

describe('describeBucket', () => {
  it('uses the wording of an offered option where one matches', () => {
    expect(describeBucket(600_000)).toBe('10 minute');
    expect(describeBucket(86_400_000)).toBe('1 day');
  });

  it('describes an arbitrary width at the largest unit it exceeds', () => {
    expect(describeBucket(15_000)).toBe('15 second');
    expect(describeBucket(1_580_000)).toBe('26.3 minute');
    expect(describeBucket(7_200_000)).toBe('2 hour');
    expect(describeBucket(250)).toBe('250ms');
  });
});
