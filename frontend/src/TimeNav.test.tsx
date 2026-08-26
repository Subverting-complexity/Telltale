import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TimeNav } from './TimeNav';
import type { ViewState } from './types';

const dayView: ViewState = { scale: 'day', year: 2026, month: 8, day: 26 };

function renderNav(view: ViewState = dayView, extra: Partial<React.ComponentProps<typeof TimeNav>> = {}) {
  return render(
    <TimeNav
      view={view}
      onNavigate={() => {}}
      onHourSelect={() => {}}
      selectedHourRange={null}
      minTs={null}
      maxTs={null}
      {...extra}
    />,
  );
}

describe('TimeNav collapse', () => {
  it('starts collapsed, summarising the scale, date and hour range in one chip', () => {
    renderNav();

    expect(screen.getByRole('button', { name: /Day · Aug 26, 2026 · All hours/ })).toBeInTheDocument();
    // The panel underneath is CSS-hidden via the (missing) "open" class —
    // App.css does the actual hiding, which jsdom doesn't apply, so this
    // asserts the class the CSS keys off rather than accessibility-tree
    // visibility.
    expect(document.querySelector('.date-panel')).not.toHaveClass('open');
  });

  it('keeps the date breadcrumb mounted (not unmounted) while collapsed', () => {
    renderNav();

    // Still present in the DOM even though its panel is visually collapsed —
    // App's own arrow-key day-paging test reads this via a raw querySelector,
    // and that only works if TimeNav never unmounts it on collapse.
    expect(document.querySelector('.breadcrumb-current')?.textContent).toBe('26');
  });

  it('expands on click, revealing the scale tabs and hour grid', async () => {
    const user = userEvent.setup();
    renderNav();

    await user.click(screen.getByRole('button', { name: /Day · Aug 26, 2026/ }));

    expect(document.querySelector('.date-panel')).toHaveClass('open');
    expect(screen.getByRole('tab', { name: 'Month' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '00:00 (no data)' })).toBeInTheDocument();
  });

  it('reflects a selected hour range in the summary once expanded', () => {
    renderNav(dayView, {
      selectedHourRange: { from: 0, to: 1, startHour: 6, endHour: 8 },
    });

    expect(screen.getByRole('button', { name: /Day · Aug 26, 2026 · 06:00–09:00/ })).toBeInTheDocument();
  });

  it('summarises non-day scales without an hour range', () => {
    renderNav({ scale: 'month', year: 2026, month: 8 }, { onHourSelect: vi.fn() });

    expect(screen.getByRole('button', { name: 'Month · Aug 2026' })).toBeInTheDocument();
  });
});
