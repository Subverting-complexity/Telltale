import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { wipeCapture, WipeError, getTimeline, isAbort } from './api';

const originalSearch = window.location.search;

/** Puts a window token in the address, the way Telltale opens its own window. */
function withToken(token: string | null) {
  const search = token === null ? '' : `?s=${token}`;
  Object.defineProperty(window, 'location', {
    value: { ...window.location, search },
    writable: true,
    configurable: true,
  });
}

beforeEach(() => {
  withToken('abc123');
});

afterEach(() => {
  withToken(originalSearch ? originalSearch.slice(3) : null);
  vi.restoreAllMocks();
});

describe('wipeCapture', () => {
  it('refuses to ask when this window has no token', async () => {
    withToken(null);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(wipeCapture({ scope: 'all' })).rejects.toBeInstanceOf(WipeError);

    // The viewer executable serves no wipe route, so a window opened there would
    // only be refused by the server. Not sending is clearer than being refused.
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('posts the scope with the window token attached', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ rowsDeleted: 3, bytesFreed: 100 }), { status: 200 }));

    const result = await wipeCapture({ scope: 'range', from: 10, to: 20 });

    expect(result).toEqual({ rowsDeleted: 3, bytesFreed: 100 });
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe('/api/capture/wipe?s=abc123');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify({ scope: 'range', from: 10, to: 20 }));
  });

  it('carries the reason a refusal gives back to the caller', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ error: 'The capture database is busy.' }), { status: 409 }));

    await expect(wipeCapture({ scope: 'all' })).rejects.toMatchObject({
      message: 'The capture database is busy.',
      status: 409,
    });
  });

  it('falls back to the status when a refusal carries no reason', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('<html>', { status: 500 }));

    await expect(wipeCapture({ scope: 'all' })).rejects.toMatchObject({ status: 500 });
  });
});

describe('getTimeline', () => {
  it('carries the abort signal through to the request', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ points: [] }), { status: 200 }));
    const controller = new AbortController();

    await getTimeline(10, 20, 60_000, controller.signal);

    const [url, init] = fetchSpy.mock.calls[0];
    expect(String(url)).toBe('/api/timeline?from=10&to=20&bucket=60000');
    expect(init?.signal).toBe(controller.signal);
  });

  it('asks for no particular width when none is given', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ points: [] }), { status: 200 }));

    await getTimeline(10, 20);

    expect(String(fetchSpy.mock.calls[0][0])).toBe('/api/timeline?from=10&to=20');
    // No signal means no `init` at all, so a caller that never aborts is
    // unchanged by abort support existing.
    expect(fetchSpy.mock.calls[0][1]).toBeUndefined();
  });
});

describe('isAbort', () => {
  it('recognises the rejection a called-off fetch produces', async () => {
    // A real abort rather than a hand-built object, so this cannot pass against a
    // shape that fetch does not actually produce.
    //
    // The URL is absolute because this environment's fetch parses the URL before
    // it looks at the signal, and a relative one rejects as a parse failure
    // instead. Nothing is sent either way: the signal is already spent.
    const controller = new AbortController();
    controller.abort();

    const error = await fetch('http://127.0.0.1:1/api/timeline', { signal: controller.signal })
      .catch(e => e);

    expect(isAbort(error)).toBe(true);
  });

  it('does not mistake a real failure for a call-off', () => {
    // This is the distinction the chart depends on. A failure clears the series
    // and shows the empty state; a call-off leaves the screen alone because a
    // newer request is already running.
    expect(isAbort(new Error('API error: 500 Internal Server Error'))).toBe(false);
    expect(isAbort(new TypeError('Failed to fetch'))).toBe(false);
  });

  it('answers false for anything that is not an error object', () => {
    expect(isAbort(null)).toBe(false);
    expect(isAbort(undefined)).toBe(false);
    expect(isAbort('AbortError')).toBe(false);
    expect(isAbort(42)).toBe(false);
  });
});
