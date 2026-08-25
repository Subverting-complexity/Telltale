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

    await user.click(screen.getByRole('radio', { name: /The day on screen/ }));

    expect(screen.getByText(/everything recorded on Monday, 3 March 2025/))
      .toBeInTheDocument();
    expect(wipeCapture).not.toHaveBeenCalled();
  });

  it('deletes the day on screen once it is confirmed', async () => {
    const user = userEvent.setup();
    const onWiped = vi.fn();
    open(day, onWiped);

    await user.click(screen.getByRole('radio', { name: /The day on screen/ }));
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

    await user.click(screen.getByRole('radio', { name: /The day on screen/ }));
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

    expect(screen.getByRole('radio', { name: /The day on screen/ })).toBeDisabled();
    expect(screen.getByText(/open a single day first/)).toBeInTheDocument();
  });
});
