import { useId } from 'react';
import { GRANULARITIES, granularityAvailability } from './granularity';
import type { GranularityId, TimelineDetail } from './granularity';

interface GranularitySelectProps {
  value: GranularityId;
  onChange: (id: GranularityId) => void;
  /** Width of the window on screen, used to work out what is legible. */
  rangeMs: number;
  /** How the last response was served; null before the first one arrives. */
  served: TimelineDetail | null;
}

/**
 * Picks how finely the timeline divides the span on screen.
 *
 * Options the current span cannot serve are shown as unavailable rather than
 * hidden. Hiding them would make the control change width as you move between a
 * day and a year, and the reason an option is out of reach is worth reading.
 *
 * They stay focusable, using `aria-disabled` rather than `disabled`, because the
 * reason has to reach a keyboard or touch user too and a `disabled` button can
 * be neither focused nor hovered. It also means focus is never yanked out from
 * under someone when the range changes beneath their cursor.
 */
export function GranularitySelect({ value, onChange, rangeMs, served }: GranularitySelectProps) {
  const labelId = useId();
  const reasonId = useId();

  return (
    <div className="granularity-select">
      <span className="nav-subgrid-label" id={labelId}>Detail</span>
      <div className="granularity-options" role="group" aria-labelledby={labelId}>
        {GRANULARITIES.map((option, i) => {
          const { available, reason } = granularityAvailability(option, rangeMs, served);
          const selected = option.id === value;
          return (
            <span key={option.id} className="granularity-option">
              <button
                type="button"
                className={`granularity-btn ${selected ? 'active' : ''}`}
                aria-disabled={!available}
                aria-pressed={selected}
                aria-describedby={available ? undefined : `${reasonId}-${i}`}
                title={reason || undefined}
                onClick={() => { if (available) onChange(option.id); }}
              >
                {option.label}
              </button>
              {!available && (
                <span className="sr-only" id={`${reasonId}-${i}`}>{reason}</span>
              )}
            </span>
          );
        })}
      </div>
    </div>
  );
}
