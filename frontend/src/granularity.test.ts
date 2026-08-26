import { describe, it, expect } from 'vitest';
import {
  GRANULARITIES, granularityById, granularityAvailability, clampNotice, describeBucket,
} from './granularity';
import type { TimelineDetail } from './granularity';

const DAY = 86_400_000;
const YEAR = 365 * DAY;

function option(id: string) {
  const found = GRANULARITIES.find(g => g.id === id);
  if (!found) throw new Error(`no such granularity: ${id}`);
  return found;
}

/** How the server answered, with sensible defaults for the fields a test ignores. */
function served(overrides: Partial<TimelineDetail> = {}): TimelineDetail {
  return { bucketMs: 0, bucketRequestMs: null, minBucketMs: 0, tierFloorMs: 5_000, ...overrides };
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
    expect(granularityAvailability(option('auto'), YEAR, served({ minBucketMs: 600_000 })).available)
      .toBe(true);
  });

  it('offers every fine option on a day served at full detail', () => {
    for (const g of GRANULARITIES) {
      if (g.id === '1d') continue;
      expect(granularityAvailability(g, DAY, served()).available, g.id).toBe(true);
    }
  });

  it('offers everything before the first response has arrived', () => {
    // The server's floors are unknown until it has answered once. Greying the
    // control out and back in on every navigation would be worse than briefly
    // offering an option the server then widens.
    for (const g of GRANULARITIES) {
      if (g.id === '1d') continue;
      expect(granularityAvailability(g, YEAR, null).available, g.id).toBe(true);
    }
  });

  it('takes the floor from the server rather than from the span on screen', () => {
    // This is the case a local estimate gets wrong. A Year view holding three
    // days of recording is 3.15e10ms wide, which by the point cap alone would
    // rule out anything under about 26 minutes. The server measures the three
    // days it actually reads and says one minute is fine, so one minute is
    // offered.
    const threeDaysInAYearView = served({ minBucketMs: 15_000 });

    expect(granularityAvailability(option('1m'), YEAR, threeDaysInAYearView).available).toBe(true);
    expect(granularityAvailability(option('10m'), YEAR, threeDaysInAYearView).available).toBe(true);

    // And what the server does rule out is still ruled out.
    expect(granularityAvailability(option('5s'), YEAR, threeDaysInAYearView).available).toBe(false);
  });

  it('blames retention for anything below the tier floor', () => {
    const rollupOnly = served({ minBucketMs: 600_000, tierFloorMs: 600_000 });

    const oneMinute = granularityAvailability(option('1m'), YEAR, rollupOnly);
    expect(oneMinute.available).toBe(false);
    expect(oneMinute.reason).toMatch(/retained/);
  });

  it('blames the point cap for anything above the tier floor the window cannot carry', () => {
    // The tiers still hold five second detail; there is just too much of it.
    const wideRaw = served({ minBucketMs: 60_000, tierFloorMs: 5_000 });

    const fiveSeconds = granularityAvailability(option('5s'), YEAR, wideRaw);
    expect(fiveSeconds.available).toBe(false);
    expect(fiveSeconds.reason).toMatch(/points/);
  });

  it('withholds a bucket too wide to draw a line with', () => {
    // One day divided into one day is a single point.
    const oneDay = granularityAvailability(option('1d'), DAY, served());
    expect(oneDay.available).toBe(false);
    expect(oneDay.reason).toMatch(/span on screen/);

    // A week of daily points is seven, which is a chart.
    expect(granularityAvailability(option('1d'), 7 * DAY, served()).available).toBe(true);
  });

  it('never reports the option in force as out of reach', () => {
    // Narrowing a day down to one selected hour leaves an hourly bucket wider
    // than the span, but it is what the chart is being drawn at, so calling it
    // unavailable would contradict the button's own pressed state.
    const HOUR = 3_600_000;
    expect(granularityAvailability(option('1h'), HOUR, served(), true).available).toBe(true);

    // Same for one the recording can no longer serve.
    const rollupOnly = served({ minBucketMs: 600_000, tierFloorMs: 600_000 });
    expect(granularityAvailability(option('5s'), YEAR, rollupOnly, true).available).toBe(true);

    // And the same options are still withheld when they are not in force.
    expect(granularityAvailability(option('1h'), HOUR, served(), false).available).toBe(false);
    expect(granularityAvailability(option('5s'), YEAR, rollupOnly, false).available).toBe(false);
  });

  it('treats an empty span as unconstrained', () => {
    expect(granularityAvailability(option('5s'), 0, served()).available).toBe(true);
  });
});

describe('clampNotice', () => {
  it('says nothing when no granularity was asked for', () => {
    expect(clampNotice(served({ bucketMs: 600_000 }))).toBeNull();
  });

  it('says nothing when the request was served as asked', () => {
    expect(clampNotice(served({ bucketMs: 600_000, bucketRequestMs: 600_000 }))).toBeNull();
  });

  it('says nothing when every recorded sample came back', () => {
    // A bucket of zero is full detail, which is never less than what was asked
    // for however fine the request was.
    expect(clampNotice(served({ bucketMs: 0, bucketRequestMs: 5_000 }))).toBeNull();
  });

  it('lowers only the first letter of the reason it splices in', () => {
    // Guards against `.toLowerCase()` on the whole sentence, which would flatten
    // any capitalised word a future reason happened to name.
    const notice = clampNotice(served({
      bucketMs: 600_000, bucketRequestMs: 5_000, tierFloorMs: 600_000,
    }))!;
    expect(notice).toContain('but finer detail');
    expect(notice).toContain('Showing 10 minute detail.');
  });

  it('blames retention when the request was below the tier floor', () => {
    const notice = clampNotice(served({
      bucketMs: 600_000, bucketRequestMs: 5_000, tierFloorMs: 600_000,
    }));
    expect(notice).toBe(
      'Showing 10 minute detail. You asked for 5 second detail, '
      + 'but finer detail is not retained this far back.');
  });

  it('blames the point cap when the tiers held the detail but the window was too wide', () => {
    const notice = clampNotice(served({
      bucketMs: 3_600_000, bucketRequestMs: 60_000, tierFloorMs: 5_000,
    }));
    expect(notice).toBe(
      'Showing 1 hour detail. You asked for 1 minute detail, '
      + 'but that would be more points than one response carries.');
  });
});

describe('describeBucket', () => {
  it('describes each offered width the way its button is labelled', () => {
    expect(describeBucket(5_000)).toBe('5 second');
    expect(describeBucket(60_000)).toBe('1 minute');
    expect(describeBucket(600_000)).toBe('10 minute');
    expect(describeBucket(3_600_000)).toBe('1 hour');
    expect(describeBucket(86_400_000)).toBe('1 day');
  });

  it('describes an arbitrary width at the largest unit it exceeds', () => {
    expect(describeBucket(15_000)).toBe('15 second');
    expect(describeBucket(1_580_000)).toBe('26.3 minute');
    expect(describeBucket(7_200_000)).toBe('2 hour');
    expect(describeBucket(250)).toBe('250ms');
  });
});
