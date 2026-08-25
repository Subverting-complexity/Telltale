import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ProcessTable } from './ProcessTable';
import type { ProcessGroupRow } from './types';

/**
 * Sixteen cores, and the process from the observed example in issue #94: one
 * recorded at 151% because the stored figure is a share of a single core. On
 * this machine that is 9.44% of everything available, which is the number the
 * table has always shown and never explained.
 */
const LOGICAL_PROCESSORS = 16;

const process: ProcessGroupRow = {
  name: 'app.exe',
  cpuPct: 151,
  privateMb: 700,
  ioKb: 1024,
  instanceCount: 1,
  path: 'C:\\Program Files\\app\\app.exe',
};

function renderTable() {
  return render(
    <ProcessTable
      processes={[process]}
      logicalProcessors={LOGICAL_PROCESSORS}
      onSelectGroup={() => {}}
      onCompare={() => {}}
      filter=""
      onFilterChange={() => {}}
      sortBy="cpu"
      onSortChange={() => {}}
      categoryFilter="all"
      onCategoryChange={() => {}}
    />,
  );
}

describe('ProcessTable CPU denominator', () => {
  it('says its CPU column is a share of all cores', () => {
    renderTable();

    expect(screen.getByRole('columnheader', { name: /CPU % of all cores/ }))
      .toBeInTheDocument();
  });

  it('shows the converted figure rather than the recorded one', () => {
    renderTable();

    expect(screen.getByText('9.44%')).toBeInTheDocument();

    // The stored value, which would read as a fault beside a machine gauge
    // that stops at 100.
    expect(screen.queryByText('151%')).not.toBeInTheDocument();
  });

  it('says the same thing to a screen reader as it does on screen', () => {
    renderTable();

    expect(screen.getByLabelText('CPU 9.44% of all cores')).toBeInTheDocument();
  });
});
