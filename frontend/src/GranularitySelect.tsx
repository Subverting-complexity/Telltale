import { GRANULARITIES, granularityAvailability } from './granularity';
import type { GranularityId } from './granularity';

interface GranularitySelectProps {
  value: GranularityId;
  onChange: (id: GranularityId) => void;
  /** Width of the window on screen, used to work out what is offerable. */
  rangeMs: number;
  /** Finest bucket the server said this window can serve; null before the first response. */
  minBucketMs: number | null;
}

/**
 * Picks how finely the timeline divides the span on screen.
 *
 * Options the current span cannot serve are disabled rather than hidden. Hiding
 * them would make the control change width as you move between a day and a
 * year, and the reason an option is missing is worth reading.
 */
export function GranularitySelect({ value, onChange, rangeMs, minBucketMs }: GranularitySelectProps) {
  return (
    <div className="granularity-select">
      <span className="nav-subgrid-label" id="granularity-label">Detail</span>
      <div className="granularity-options" role="group" aria-labelledby="granularity-label">
        {GRANULARITIES.map(option => {
          const { available, reason } = granularityAvailability(option, rangeMs, minBucketMs);
          const selected = option.id === value;
          return (
            <button
              key={option.id}
              type="button"
              className={`granularity-btn ${selected ? 'active' : ''}`}
              disabled={!available}
              aria-pressed={selected}
              title={reason || undefined}
              aria-label={available ? option.label : `${option.label} (unavailable: ${reason})`}
              onClick={() => onChange(option.id)}
            >
              {option.label}
            </button>
          );
        })}
      </div>
    </div>
  );
}
