/**
 * Tells the Telltale application whether this window is still open.
 *
 * The single-process build serves the API only while there is a window to serve
 * it to, and it cannot see the window to find out. Starting a browser with
 * `--app` hands the request to the browser that is already running, so the
 * process Telltale started exits within moments while the window stays on
 * screen. The page is therefore the only thing that knows, and this is how it
 * says so: a ping while it is open, and one message on the way out.
 *
 * Two things travel with each message. A token, which Telltale put in the URL it
 * opened this window on, and which no page from anywhere else can read. And an id
 * for this window, so that closing one window does not stop the server under
 * another one that is still open.
 *
 * Without the token any page the user happened to have open in another tab could
 * post to the closing endpoint and take this window's server away, or poll the
 * ping endpoint and hold it open for exactly the hours it is meant to be shut.
 * Neither needs to read a reply to work, so a browser sends both without asking
 * permission first.
 *
 * The standalone viewer executable does not serve these paths and opens no
 * window of its own, so there is no token, and this does nothing at all.
 */

/** How often an open window says it is still there. */
export const PING_INTERVAL_MS = 15_000;

const PING_PATH = '/api/session/ping';
const CLOSE_PATH = '/api/session/closed';

export interface KeepaliveOptions {
  /** The token Telltale put in this window's URL. Absent means do nothing. */
  token?: string | null;
  /** Identifies this window. Defaults to a fresh random id per page load. */
  windowId?: string;
  /** Sends a one-way POST that expects no reply. */
  send?: (path: string) => void;
  /** Subscribes to the page going away, and returns the unsubscribe. */
  onPageHide?: (listener: () => void) => () => void;
  /** Subscribes to the page coming back to life, and returns the unsubscribe. */
  onWake?: (listener: () => void) => () => void;
}

/**
 * Starts telling the application this window is open.
 *
 * @returns A function that stops doing so. It does not send the closing
 * message: stopping the keepalive is what a component unmounting does, and that
 * is not the same event as the window going away.
 */
export function startSessionKeepalive(options: KeepaliveOptions = {}): () => void {
  const token = options.token !== undefined ? options.token : tokenFromUrl();
  if (!token) return () => {};

  const windowId = options.windowId ?? newWindowId();
  const send = options.send ?? sendOneWay;
  const onPageHide = options.onPageHide ?? subscribeToPageHide;
  const onWake = options.onWake ?? subscribeToWake;

  const query = `?s=${encodeURIComponent(token)}&c=${encodeURIComponent(windowId)}`;
  const ping = () => send(PING_PATH + query);

  // Sent immediately as well as on the interval, so a window that opens and is
  // closed again inside the first interval has still been seen.
  ping();
  const timer = setInterval(ping, PING_INTERVAL_MS);
  const stopHideListening = onPageHide(() => send(CLOSE_PATH + query));

  // A machine that has been asleep comes back with this window long overdue and
  // the interval not yet due again. Saying so at once, rather than waiting up to
  // another fifteen seconds, is what keeps the application from concluding the
  // window went away while it was not looking.
  const stopWakeListening = onWake(ping);

  return () => {
    clearInterval(timer);
    stopHideListening();
    stopWakeListening();
  };
}

/** Reads the token Telltale put in the URL it opened this window on. */
export function tokenFromUrl(): string | null {
  if (typeof location === 'undefined') return null;
  try {
    return new URLSearchParams(location.search).get('s');
  } catch {
    return null;
  }
}

/**
 * A fresh id for this page load.
 *
 * It only has to be different from the other windows Telltale has open, so a
 * random value is enough and `randomUUID` is not worth requiring: it is missing
 * on any browser serving this page over plain http without a secure context.
 */
function newWindowId(): string {
  const random = Math.random().toString(36).slice(2);
  return `${Date.now().toString(36)}-${random}`;
}

/**
 * Posts without waiting for or caring about the answer.
 *
 * `sendBeacon` is used where it exists because it survives the page being torn
 * down, which is exactly the moment the closing message is sent.
 */
function sendOneWay(path: string): void {
  // sendBeacon returns false when the browser declines to queue the request,
  // which is a refusal rather than a delivery. Falling through to fetch is what
  // keeps a run of those from looking like the window having gone away.
  if (typeof navigator !== 'undefined' && typeof navigator.sendBeacon === 'function') {
    if (navigator.sendBeacon(path)) return;
  }

  // A failure here means an application that has already gone, which is not
  // something this page can do anything about.
  void fetch(path, { method: 'POST', keepalive: true }).catch(() => {});
}

/**
 * Listens for the page going away.
 *
 * `pagehide` rather than `beforeunload`, because `beforeunload` does not fire
 * when a page is restored from the back/forward cache and is treated as a
 * blocking hook by some browsers.
 */
function subscribeToPageHide(listener: () => void): () => void {
  window.addEventListener('pagehide', listener);
  return () => window.removeEventListener('pagehide', listener);
}

/**
 * Listens for the page becoming current again, after a sleep, a minimise or a
 * switch to another window.
 */
function subscribeToWake(listener: () => void): () => void {
  const onVisible = () => {
    if (document.visibilityState === 'visible') listener();
  };

  document.addEventListener('visibilitychange', onVisible);
  window.addEventListener('pageshow', listener);
  window.addEventListener('focus', listener);

  return () => {
    document.removeEventListener('visibilitychange', onVisible);
    window.removeEventListener('pageshow', listener);
    window.removeEventListener('focus', listener);
  };
}
