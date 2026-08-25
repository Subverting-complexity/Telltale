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
 * The standalone viewer executable does not serve these paths and answers 404.
 * That is expected and ignored, because a viewer with no listener to shut down
 * has nothing to do with the answer.
 */

/** How often an open window says it is still there. */
export const PING_INTERVAL_MS = 15_000;

const PING_PATH = '/api/session/ping';
const CLOSE_PATH = '/api/session/closed';

export interface KeepaliveOptions {
  /** Sends a one-way POST that expects no reply. */
  send?: (path: string) => void;
  /** Subscribes to the page going away, and returns the unsubscribe. */
  onPageHide?: (listener: () => void) => () => void;
}

/**
 * Starts telling the application this window is open.
 *
 * @returns A function that stops doing so. It does not send the closing
 * message: stopping the keepalive is what a component unmounting does, and that
 * is not the same event as the window going away.
 */
export function startSessionKeepalive(options: KeepaliveOptions = {}): () => void {
  const send = options.send ?? sendOneWay;
  const onPageHide = options.onPageHide ?? subscribeToPageHide;

  // Sent immediately as well as on the interval, so a window that opens and is
  // closed again inside the first interval has still been seen.
  send(PING_PATH);
  const timer = setInterval(() => send(PING_PATH), PING_INTERVAL_MS);
  const stopListening = onPageHide(() => send(CLOSE_PATH));

  return () => {
    clearInterval(timer);
    stopListening();
  };
}

/**
 * Posts without waiting for or caring about the answer.
 *
 * `sendBeacon` is used where it exists because it survives the page being torn
 * down, which is exactly the moment the closing message is sent.
 */
function sendOneWay(path: string): void {
  if (typeof navigator !== 'undefined' && typeof navigator.sendBeacon === 'function') {
    navigator.sendBeacon(path);
    return;
  }

  // A failure here means the standalone viewer, or an application that has
  // already gone. Neither is something this page can do anything about.
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
