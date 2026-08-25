import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { startSessionKeepalive, PING_INTERVAL_MS } from './session';

describe('session keepalive', () => {
  let sent: string[];
  let pageHideListener: (() => void) | null;
  let unsubscribed: boolean;

  const options = () => ({
    send: (path: string) => { sent.push(path); },
    onPageHide: (listener: () => void) => {
      pageHideListener = listener;
      return () => { unsubscribed = true; };
    },
  });

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

    expect(sent).toEqual(['/api/session/ping']);
  });

  it('keeps saying so while the window stays open', () => {
    startSessionKeepalive(options());

    vi.advanceTimersByTime(PING_INTERVAL_MS * 3);

    expect(sent).toEqual([
      '/api/session/ping',
      '/api/session/ping',
      '/api/session/ping',
      '/api/session/ping',
    ]);
  });

  it('sends one closing message when the page goes away', () => {
    startSessionKeepalive(options());
    sent.length = 0;

    pageHideListener?.();

    expect(sent).toEqual(['/api/session/closed']);
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
    expect(sent).toEqual(['/api/session/ping']);
  });

  it('pings well inside the timeout the application uses', () => {
    // The application gives up on a window after 90 seconds of silence. The
    // interval has to leave room for a ping or two to be lost on the way.
    expect(PING_INTERVAL_MS).toBeLessThan(90_000 / 3);
  });
});
