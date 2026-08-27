import { useEffect, useRef, useState } from 'react';
import { wipeCapture, WipeError } from './api';
import type { WipeResponse } from './types';

export interface WipeTarget {
  /** The date as a person reads it, used in the confirmation. */
  label: string;
  /** Start of the day, epoch milliseconds, included. */
  from: number;
  /** End of the day, epoch milliseconds, included. */
  to: number;
}

export interface WipeDataDialogProps {
  /** The day currently on screen, or null when the view is not on one day. */
  day: WipeTarget | null;
  /** Closes the dialog without doing anything further. */
  onClose: () => void;
  /** Called after data has actually gone, so the view can reload. */
  onWiped: () => void;
}

type Choice = 'day' | 'all';

/**
 * What is added when the space has been released but the folder has not shrunk.
 *
 * Telltale tidies up after a delete by folding its write ahead log back into the
 * capture file and shortening both, and something reading the recording at that
 * moment holds the shortening off. The rows have gone and the space is genuinely
 * released, so the figure is early rather than wrong, but someone who goes and
 * looks at the folder sees no change and on a large delete briefly sees it grow.
 *
 * Saying so is the answer chosen in #176. The alternative was reporting nothing
 * freed, which is honest about the folder at that moment and tells a person who
 * has just deleted a year of history that they got nothing back, which is the
 * more damaging of the two ways to be wrong.
 *
 * No time is promised. What clears it is a tidy-up that nothing interrupts, and
 * how long that takes depends on whether the window is being used.
 */
const PENDING_NOTE =
  ' The folder has not shrunk yet, because Telltale was reading the recording '
  + 'while the delete ran. The space comes back on its own at the next tidy-up '
  + 'that nothing interrupts.';

/**
 * What went, as a sentence.
 *
 * The space freed is left out rather than reported as zero when there is none.
 * SQLite hands pages back a whole page at a time, so a small delete genuinely
 * frees nothing measurable, and saying "freeing 0 MB" reads as a failure when it
 * is not one.
 */
function describeResult(rowsDeleted: number, bytesFreed: number, spacePending: boolean): string {
  if (rowsDeleted === 0) {
    return 'There was nothing recorded in that range, so nothing was deleted.';
  }

  const rows = `Deleted ${rowsDeleted.toLocaleString()} recorded ${rowsDeleted === 1 ? 'row' : 'rows'}`;
  const pending = spacePending ? PENDING_NOTE : '';
  if (bytesFreed <= 0) return `${rows}.${pending}`;

  const space = bytesFreed < 1024 * 1024
    ? `${Math.max(1, Math.round(bytesFreed / 1024))} KB`
    : `${(bytesFreed / (1024 * 1024)).toFixed(1)} MB`;
  return `${rows}, freeing ${space}.${pending}`;
}

/**
 * Asks what should be thrown away, then asks again before doing it.
 *
 * Deleting a recording cannot be undone, so it takes two deliberate answers: one
 * choosing what goes, and one confirming it by name. Neither is the dialog's
 * default action, so nothing here happens to a stray click or a stray Enter.
 */
export function WipeDataDialog({ day, onClose, onWiped }: WipeDataDialogProps) {
  const [choice, setChoice] = useState<Choice | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<WipeResponse | null>(null);
  const panel = useRef<HTMLDivElement>(null);

  // Focus moves in on open and back to whatever had it on close. Without the
  // second half, closing the dialog drops focus onto the body and a keyboard
  // user restarts from the top of the page.
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    return () => previous?.focus?.();
  }, []);

  // Both of these take the focused control away: pressing Delete disables it
  // while the wipe runs, and finishing unmounts it. A browser drops focus to the
  // body when that happens, which puts it outside a dialog that has told
  // assistive technology the rest of the page is not there. Taking it back keeps
  // the reading order inside the dialog and puts the result in reach.
  useEffect(() => {
    if (!panel.current?.contains(document.activeElement)) panel.current?.focus();
  }, [busy, done]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) {
        onClose();
        return;
      }

      // aria-modal says the rest of the page is not there. Tab has to agree with
      // it, or the next press walks out of the dialog and into content a screen
      // reader has just been told to ignore.
      if (event.key !== 'Tab' || !panel.current) return;

      // Filtered on the disabled property rather than selected with :enabled, for
      // two reasons. The property is true for a radio inside the disabled
      // fieldset, which a delete in progress produces and the [disabled]
      // attribute misses. And a selector list carrying a pseudo class comes back
      // from jsdom grouped by selector rather than in document order, which puts
      // the wrong element at each end of the ring and lets Tab out of the dialog
      // in exactly the tests written to prove it cannot.
      const stops = [...panel.current.querySelectorAll<HTMLButtonElement | HTMLInputElement>(
        'button, input')].filter(stop => !stop.disabled);
      if (stops.length === 0) return;

      const first = stops[0];
      const last = stops[stops.length - 1];
      const active = document.activeElement;

      // Focus has already left, which the two state changes on the normal path
      // both cause: the button being pressed is disabled while the delete runs,
      // and unmounted when it finishes, and a browser drops focus to the body
      // either way. Without this, the next Tab is a boundary case for neither
      // end of the ring and walks out of the dialog.
      if (!(active instanceof Node) || !panel.current.contains(active)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
        return;
      }

      if (event.shiftKey && (active === first || active === panel.current)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    };

    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [busy, onClose]);

  async function confirm() {
    if (!choice || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await wipeCapture(
        choice === 'all' ? { scope: 'all' } : { scope: 'range', from: day!.from, to: day!.to },
      );
      setDone(result);
      onWiped();
    } catch (err) {
      setError(err instanceof WipeError ? err.message : 'Nothing was deleted. The request failed.');
    } finally {
      setBusy(false);
    }
  }

  const describe = choice === 'all'
    ? 'everything Telltale has recorded'
    : day
      ? `everything recorded on ${day.label}`
      : '';

  return (
    <div className="dialog-backdrop" onClick={() => { if (!busy) onClose(); }}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="wipe-dialog-title"
        tabIndex={-1}
        ref={panel}
        onClick={event => event.stopPropagation()}
      >
        <h2 id="wipe-dialog-title">Delete recorded data</h2>

        {/*
          Present from the moment the dialog opens rather than inserted along
          with its first sentence. A live region generally has to be in the
          document before its content changes for the change to be announced, so
          one that arrives already full is usually announced as nothing at all.
        */}
        <p className={`dialog-say ${!done && !busy && choice ? 'warning' : ''}`} aria-live="polite">
          {done
            ? describeResult(done.rowsDeleted, done.bytesFreed, done.spacePending)
            : busy
              ? 'Deleting. This can take a minute on a large recording, and this window '
                + 'will say when it is done.'
              : choice
                ? `This deletes ${describe}. Are you sure?`
                : ''}
        </p>

        {/*
          The only thing on screen that says the window is still working.
          Deleting a large recording runs for tens of seconds inside one request,
          with nothing to report until it returns, and every control here is
          disabled while it does. Without this the dialog reads as frozen, and a
          person watching a button that says Deleting and does nothing else is
          left deciding whether to close the window on a delete that is still
          running.
        */}
        {busy && (
          <div className="dialog-progress" aria-hidden="true"><span /></div>
        )}

        {done ? (
          <>
            <div className="dialog-actions">
              <button className="dialog-btn" onClick={onClose}>Close</button>
            </div>
          </>
        ) : (
          <>
            <p className="dialog-note">
              Recorded data stays on this machine, and deleting it cannot be undone.
              Recording carries on either way.
            </p>

            <fieldset className="dialog-choices" disabled={busy}>
              <legend>What should go?</legend>

              <label className={`dialog-choice ${day ? '' : 'unavailable'}`}>
                <input
                  type="radio"
                  name="wipe-scope"
                  value="day"
                  checked={choice === 'day'}
                  disabled={!day}
                  onChange={() => setChoice('day')}
                />
                <span>
                  {day ? `The whole day being viewed (${day.label})` : 'The whole day being viewed'}
                  {!day && (
                    <span className="dialog-hint"> (open a single day first)</span>
                  )}
                </span>
              </label>

              <label className="dialog-choice">
                <input
                  type="radio"
                  name="wipe-scope"
                  value="all"
                  checked={choice === 'all'}
                  onChange={() => setChoice('all')}
                />
                <span>Everything recorded so far</span>
              </label>
            </fieldset>

            {error && <p className="dialog-error" role="alert">{error}</p>}

            <div className="dialog-actions">
              <button className="dialog-btn" onClick={onClose} disabled={busy}>
                Cancel
              </button>
              <button
                className="dialog-btn danger"
                onClick={confirm}
                disabled={!choice || busy}
              >
                {busy ? 'Deleting...' : 'Delete permanently'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
