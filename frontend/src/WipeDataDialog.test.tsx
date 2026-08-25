import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WipeDataDialog } from './WipeDataDialog';
import type { WipeTarget } from './WipeDataDialog';

const wipeCapture = vi.fn();

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return { ...actual, wipeCapture: (...args: unknown[]) => wipeCapture(...args) };
});

const day: WipeTarget = {
  label: 'Monday, 3 March 2025',
  from: 1_740_960_000_000,
  to: 1_741_046_399_999,
};

function open(target: WipeTarget | null = day, onWiped = () => {}) {
  return render(<WipeDataDialog day={target} onClose={() => {}} onWiped={onWiped} />);
}

beforeEach(() => {
  wipeCapture.mockReset();
  wipeCapture.mockResolvedValue({ rowsDeleted: 42, bytesFreed: 2 * 1024 * 1024 });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('WipeDataDialog', () => {
  it('will not delete anything until a scope has been chosen', async () => {
    open();

    // The dialog opens with its destructive action already unavailable, so the
    // click that opened it cannot carry through into a delete.
    expect(screen.getByRole('button', { name: 'Delete permanently' })).toBeDisabled();
    expect(wipeCapture).not.toHaveBeenCalled();
  });

  it('names the day before deleting it', async () => {
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /The whole day being viewed/ }));

    expect(screen.getByText(/everything recorded on Monday, 3 March 2025/))
      .toBeInTheDocument();
    expect(wipeCapture).not.toHaveBeenCalled();
  });

  it('deletes the day on screen once it is confirmed', async () => {
    const user = userEvent.setup();
    const onWiped = vi.fn();
    open(day, onWiped);

    await user.click(screen.getByRole('radio', { name: /The whole day being viewed/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    await waitFor(() => expect(wipeCapture).toHaveBeenCalledWith({
      scope: 'range', from: day.from, to: day.to,
    }));
    expect(onWiped).toHaveBeenCalled();
  });

  it('deletes everything when that is what was chosen', async () => {
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /Everything recorded so far/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    await waitFor(() => expect(wipeCapture).toHaveBeenCalledWith({ scope: 'all' }));
  });

  it('reports what went', async () => {
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /Everything recorded so far/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    expect(await screen.findByText(/Deleted 42 recorded rows, freeing 2\.0 MB/))
      .toBeInTheDocument();
  });

  it('says nothing was there rather than reporting an empty delete as a success', async () => {
    wipeCapture.mockResolvedValue({ rowsDeleted: 0, bytesFreed: 0 });
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /The whole day being viewed/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    expect(await screen.findByText(/nothing was deleted/)).toBeInTheDocument();
  });

  it('shows the reason a refused delete gives, and leaves the dialog open', async () => {
    const { WipeError } = await import('./api');
    wipeCapture.mockRejectedValue(
      new WipeError('The capture database is busy. Try again in a moment.', 409));
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /Everything recorded so far/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/busy/);
    expect(screen.getByRole('button', { name: 'Delete permanently' })).toBeEnabled();
  });

  it('does not offer to delete a day when the view is not on one', () => {
    open(null);

    expect(screen.getByRole('radio', { name: /The whole day being viewed/ })).toBeDisabled();
    expect(screen.getByText(/open a single day first/)).toBeInTheDocument();
  });

  it('keeps Tab inside the dialog', async () => {
    const user = userEvent.setup();
    open();

    // aria-modal tells assistive technology the rest of the page is not there.
    // Tab has to agree, or the next press walks into content the user has just
    // been told to ignore, with the dialog still on screen.
    for (let press = 0; press < 8; press++) {
      await user.tab();
      expect(screen.getByRole('dialog').contains(document.activeElement)).toBe(true);
    }
  });

  it('keeps focus inside once the delete has finished', async () => {
    const user = userEvent.setup();
    open();

    await user.click(screen.getByRole('radio', { name: /Everything recorded so far/ }));
    await user.click(screen.getByRole('button', { name: 'Delete permanently' }));
    await screen.findByRole('button', { name: 'Close' });

    // The button that was pressed is unmounted when the delete finishes, and a
    // browser drops focus to the body when that happens. Left there, the next
    // Tab is a boundary case for neither end of the ring and walks straight out
    // of a dialog that has told assistive technology the page behind it is not
    // there, taking the result with it.
    const dialog = screen.getByRole('dialog');
    expect(dialog.contains(document.activeElement)).toBe(true);

    await user.tab();
    expect(dialog.contains(document.activeElement)).toBe(true);
  });

  it('gives focus back to whatever had it', async () => {
    const opener = document.createElement('button');
    document.body.appendChild(opener);
    opener.focus();

    const { unmount } = open();
    expect(document.activeElement).not.toBe(opener);

    unmount();

    expect(document.activeElement).toBe(opener);
    opener.remove();
  });

  it('says what it is about to do through a region that was already there', async () => {
    const user = userEvent.setup();
    const { container } = open();

    // The region has to be in the document before its text changes, or the
    // change is not announced. Rendered along with its first sentence, it is
    // usually announced as nothing at all.
    const region = container.querySelector('[aria-live="polite"]');
    expect(region).not.toBeNull();
    expect(region).toHaveTextContent('');

    await user.click(screen.getByRole('radio', { name: /Everything recorded so far/ }));

    expect(region).toHaveTextContent(/everything Telltale has recorded/);
  });
});
