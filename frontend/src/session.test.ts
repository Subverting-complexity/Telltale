import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { startSessionKeepalive, PING_INTERVAL_MS } from './session';

describe('session keepalive', () => {
  let sent: string[];
  let pageHideListener: (() => void) | null;
  let unsubscribed: boolean;

  const options = (extra: Record<string, unknown> = {}) => ({
    token: 'TOKEN123',
    windowId: 'window-a',
    send: (path: string) => { sent.push(path); },
    onPageHide: (listener: () => void) => {
      pageHideListener = listener;
      return () => { unsubscribed = true; };
    },
    ...extra,
  });

  const ping = '/api/session/ping?s=TOKEN123&c=window-a';
  const closed = '/api/session/closed?s=TOKEN123&c=window-a';

  beforeEach(() => {
    sent = [];
    pageHideListener = null;
    unsubscribed = false;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('says the window is open straight away', () => {
    // A window opened and closed again inside the first interval still has to
    // have been seen, or the listener shuts down under a live page.
    startSessionKeepalive(options());

    expect(sent).toEqual([ping]);
  });

  it('keeps saying so while the window stays open', () => {
    startSessionKeepalive(options());

    vi.advanceTimersByTime(PING_INTERVAL_MS * 3);

    expect(sent).toEqual([ping, ping, ping, ping]);
  });

  it('sends one closing message when the page goes away', () => {
    startSessionKeepalive(options());
    sent.length = 0;

    pageHideListener?.();

    expect(sent).toEqual([closed]);
  });

  it('identifies this window on every message', () => {
    // Without an id per window, closing one window would stop the server under
    // another one that is still open.
    startSessionKeepalive(options({ windowId: 'window-b' }));
    pageHideListener?.();

    expect(sent).toEqual([
      '/api/session/ping?s=TOKEN123&c=window-b',
      '/api/session/closed?s=TOKEN123&c=window-b',
    ]);
  });

  it('carries the token Telltale put in the URL', () => {
    startSessionKeepalive(options({ token: 'a b&c=d' }));

    expect(sent).toEqual(['/api/session/ping?s=a%20b%26c%3Dd&c=window-a']);
  });

  it('does nothing at all without a token', () => {
    // This is the standalone viewer, which serves no session endpoints and has
    // no listener lifetime to manage. Pinging it would be noise.
    const stop = startSessionKeepalive(options({ token: null }));

    vi.advanceTimersByTime(PING_INTERVAL_MS * 5);
    stop();

    expect(sent).toEqual([]);
  });

  it('stops pinging once it has been stopped', () => {
    const stop = startSessionKeepalive(options());
    sent.length = 0;

    stop();
    vi.advanceTimersByTime(PING_INTERVAL_MS * 5);

    expect(sent).toEqual([]);
    expect(unsubscribed).toBe(true);
  });

  it('does not report a close when it is merely stopped', () => {
    // Unmounting is not the window going away. Reporting a close here would
    // shut the listener down while the page is still on screen.
    const stop = startSessionKeepalive(options());
    sent.length = 0;

    stop();

    expect(sent).toEqual([]);
  });

  it('pings on its own interval and no faster', () => {
    startSessionKeepalive(options());
    sent.length = 0;

    vi.advanceTimersByTime(PING_INTERVAL_MS - 1);
    expect(sent).toEqual([]);

    vi.advanceTimersByTime(1);
    expect(sent).toEqual([ping]);
  });

  it('pings well inside the timeout the application uses', () => {
    // The application gives up on a window after 90 seconds of silence. The
    // interval has to leave room for a ping or two to be lost on the way.
    expect(PING_INTERVAL_MS).toBeLessThan(90_000 / 3);
  });
});
