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
 * What went, as a sentence.
 *
 * The space freed is left out rather than reported as zero when there is none.
 * SQLite hands pages back a whole page at a time, so a small delete genuinely
 * frees nothing measurable, and saying "freeing 0 MB" reads as a failure when it
 * is not one.
 */
function describeResult(rowsDeleted: number, bytesFreed: number): string {
  if (rowsDeleted === 0) {
    return 'There was nothing recorded in that range, so nothing was deleted.';
  }

  const rows = `Deleted ${rowsDeleted.toLocaleString()} recorded ${rowsDeleted === 1 ? 'row' : 'rows'}`;
  if (bytesFreed <= 0) return `${rows}.`;

  const space = bytesFreed < 1024 * 1024
    ? `${Math.max(1, Math.round(bytesFreed / 1024))} KB`
    : `${(bytesFreed / (1024 * 1024)).toFixed(1)} MB`;
  return `${rows}, freeing ${space}.`;
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

  useEffect(() => {
    panel.current?.focus();
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onClose();
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

        {done ? (
          <>
            <p aria-live="polite">{describeResult(done.rowsDeleted, done.bytesFreed)}</p>
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
                  {day ? `The day on screen (${day.label})` : 'The day on screen'}
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

            {choice && (
              <p className="dialog-confirm" aria-live="polite">
                This deletes {describe}. Are you sure?
              </p>
            )}

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
