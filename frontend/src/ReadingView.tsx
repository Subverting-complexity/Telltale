/**
 * Which reading a summary panel answers on.
 *
 * `now` is the newest reading inside the range on screen, which on a range that
 * ends in the past is the newest reading in that range rather than this moment.
 * `over-time` is the whole range.
 *
 * Every panel that offers the choice defaults to `now` and keeps its own state,
 * so the tiles and Top Consumers can sit on different views at once. The choice
 * is deliberately not remembered between windows: the panels exist to answer
 * what is using the machine right now, which is the question someone has on
 * opening, and a restored `over-time` would quietly answer a different one.
 */
export type ReadingView = 'now' | 'over-time';

const OPTIONS: readonly { id: ReadingView; label: string }[] = [
  { id: 'now', label: 'Now' },
  { id: 'over-time', label: 'Over time' },
];

interface ReadingViewToggleProps {
  value: ReadingView;
  onChange: (view: ReadingView) => void;
  /** Names what is being switched, for anyone who cannot see which panel it sits in. */
  label: string;
}

export function ReadingViewToggle({ value, onChange, label }: ReadingViewToggleProps) {
  return (
    <div className="reading-toggle-group" role="radiogroup" aria-label={label}>
      {OPTIONS.map(option => (
        <button
          key={option.id}
          type="button"
          className={`reading-toggle ${value === option.id ? 'active' : ''}`}
          onClick={() => onChange(option.id)}
          role="radio"
          aria-checked={value === option.id}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}
