import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { startSessionKeepalive, PING_INTERVAL_MS } from './session';

describe('session keepalive', () => {
  let sent: string[];
  let pageHideListener: (() => void) | null;
  let wakeListener: (() => void) | null;
  let unsubscribed: boolean;
  let wakeUnsubscribed: boolean;

  const options = (extra: Record<string, unknown> = {}) => ({
    token: 'TOKEN123',
    windowId: 'window-a',
    send: (path: string) => { sent.push(path); },
    onPageHide: (listener: () => void) => {
      pageHideListener = listener;
      return () => { unsubscribed = true; };
    },
    onWake: (listener: () => void) => {
      wakeListener = listener;
      return () => { wakeUnsubscribed = true; };
    },
    ...extra,
  });

  const ping = '/api/session/ping?s=TOKEN123&c=window-a';
  const closed = '/api/session/closed?s=TOKEN123&c=window-a';

  beforeEach(() => {
    sent = [];
    pageHideListener = null;
    wakeListener = null;
    unsubscribed = false;
    wakeUnsubscribed = false;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
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

  it('says it is still here as soon as the page wakes up', () => {
    // A machine coming back from sleep has left this window overdue and the
    // interval not yet due again. Waiting up to another fifteen seconds is long
    // enough for the application to conclude the window went away.
    startSessionKeepalive(options());
    sent.length = 0;

    wakeListener?.();

    expect(sent).toEqual([ping]);
  });

  it('stops listening for wake-ups once it has been stopped', () => {
    // A subscription left behind would keep pinging for a window that has gone.
    const stop = startSessionKeepalive(options());

    stop();

    expect(wakeUnsubscribed).toBe(true);
  });

  it('reads the token out of the address the window was opened on', () => {
    // The only path the shipped build takes. Everything else here passes the
    // token in, so without this the real one is never exercised.
    vi.stubGlobal('location', { search: '?s=FROM-URL' });

    startSessionKeepalive({
      windowId: 'window-a',
      send: (path: string) => { sent.push(path); },
      onPageHide: () => () => {},
      onWake: () => () => {},
    });

    expect(sent).toEqual(['/api/session/ping?s=FROM-URL&c=window-a']);
  });

  it('does nothing when the address carries no token', () => {
    // Someone typed the address in, or this is the standalone viewer.
    vi.stubGlobal('location', { search: '' });

    startSessionKeepalive({
      windowId: 'window-a',
      send: (path: string) => { sent.push(path); },
      onPageHide: () => () => {},
      onWake: () => () => {},
    });

    expect(sent).toEqual([]);
  });

  it('pings well inside the timeout the application uses', () => {
    // The application gives up on a window after 90 seconds of silence, which
    // host/ViewerListener.cs asserts from its side. This is the other half: the
    // interval has to leave room for a ping or two to be lost on the way.
    expect(PING_INTERVAL_MS).toBeLessThan(90_000 / 3);
  });
});
