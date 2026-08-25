import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { wipeCapture, WipeError } from './api';

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
