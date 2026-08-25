import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TopConsumers } from './TopConsumers';
import type { ProcessGroupRow } from './types';

/** Sixteen cores, and a process recorded at 151% of one of them: 9.44% here. */
const LOGICAL_PROCESSORS = 16;

const busyProcess: ProcessGroupRow = {
  name: 'app.exe',
  cpuPct: 151,
  privateMb: 700,
  ioKb: 1024,
  instanceCount: 1,
  path: 'C:\\Program Files\\app\\app.exe',
};

function renderPanel() {
  return render(
    <TopConsumers
      processes={[busyProcess]}
      logicalProcessors={LOGICAL_PROCESSORS}
      onSelectProcess={() => {}}
      categoryFilter="all"
    />,
  );
}

describe('TopConsumers CPU denominator', () => {
  it('says which scale it ranks processes on', () => {
    // The panel converts and always did. What it never said was what the
    // resulting percentage is a percentage of.
    renderPanel();

    expect(screen.getByText(/ranked by CPU, as a share of all cores/))
      .toBeInTheDocument();
  });

  it('shows the converted figure rather than the recorded one', () => {
    renderPanel();

    expect(screen.getByText('9.44%')).toBeInTheDocument();
    expect(screen.queryByText('151%')).not.toBeInTheDocument();
  });
});
