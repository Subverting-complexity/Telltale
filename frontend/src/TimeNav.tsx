import { useEffect, useState } from 'react';
import type { ViewState, ViewScale } from './types';
import { getDaysInMonth } from './utils';
import { GranularitySelect } from './GranularitySelect';
import type { GranularityId } from './granularity';

export interface HourSelection {
  from: number;
  to: number;
  startHour: number;
  endHour: number;
}

interface HourRange {
  startHour: number;
  endHour: number;
}

interface TimeNavProps {
  view: ViewState;
  onNavigate: (view: ViewState) => void;
  onHourSelect?: (selection: HourSelection | null) => void;
  selectedHourRange?: HourSelection | null;
  minTs: number | null;
  maxTs: number | null;
  granularity: GranularityId;
  onGranularityChange: (id: GranularityId) => void;
  /** Width of the window on screen, for deciding which granularities are offerable. */
  rangeMs: number;
  /** Finest bucket the last response said this window can serve; null before the first one. */
  minBucketMs: number | null;
}

const SCALES: ViewScale[] = ['day', 'week', 'month', 'year'];
const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export function pad2(n: number): string {
  return String(n).padStart(2, '0');
}

// getWeekRange in utils.ts anchors a week on the Sunday on or before `day`
// and runs 7 days from there; this mirrors that boundary so the collapsed
// label describes the same span the view actually shows, rather than the
// single anchor date (which reads as a day-scale label, not a week one).
function formatWeekRange(year: number, month: number, day: number): string {
  const anchor = new Date(year, month - 1, day);
  const start = new Date(year, month - 1, day - anchor.getDay());
  const end = new Date(start.getFullYear(), start.getMonth(), start.getDate() + 6);

  if (start.getFullYear() === end.getFullYear()) {
    if (start.getMonth() === end.getMonth()) {
      return `${MONTH_NAMES[start.getMonth()]} ${start.getDate()}–${end.getDate()}, ${end.getFullYear()}`;
    }
    return `${MONTH_NAMES[start.getMonth()]} ${start.getDate()} – ${MONTH_NAMES[end.getMonth()]} ${end.getDate()}, ${end.getFullYear()}`;
  }
  return `${MONTH_NAMES[start.getMonth()]} ${start.getDate()}, ${start.getFullYear()} – ${MONTH_NAMES[end.getMonth()]} ${end.getDate()}, ${end.getFullYear()}`;
}

function summaryLabel(view: ViewState, selectedHourRange?: HourSelection | null): string {
  const scaleLabel = view.scale.charAt(0).toUpperCase() + view.scale.slice(1);

  const dateLabel = view.scale === 'year' || !view.month
    ? String(view.year)
    : view.scale === 'week' && view.day
    ? formatWeekRange(view.year, view.month, view.day)
    : view.scale === 'month' || !view.day
    ? `${MONTH_NAMES[view.month - 1]} ${view.year}`
    : `${MONTH_NAMES[view.month - 1]} ${view.day}, ${view.year}`;

  if (view.scale !== 'day') return `${scaleLabel} · ${dateLabel}`;

  const hourLabel = !selectedHourRange
    ? 'All hours'
    : selectedHourRange.startHour === selectedHourRange.endHour
    ? `${pad2(selectedHourRange.startHour)}:00–${pad2(selectedHourRange.startHour + 1)}:00`
    : `${pad2(selectedHourRange.startHour)}:00–${pad2(selectedHourRange.endHour + 1)}:00`;

  return `${scaleLabel} · ${dateLabel} · ${hourLabel}`;
}

export function TimeNav({
  view, onNavigate, onHourSelect, selectedHourRange, minTs, maxTs,
  granularity, onGranularityChange, rangeMs, minBucketMs,
}: TimeNavProps) {
  const now = new Date();
  const [expanded, setExpanded] = useState(false);

  function prev() {
    switch (view.scale) {
      case 'year':
        onNavigate({ ...view, year: view.year - 1 });
        break;
      case 'month':
        if (view.month === 1) onNavigate({ ...view, year: view.year - 1, month: 12 });
        else onNavigate({ ...view, month: (view.month ?? 1) - 1 });
        break;
      case 'week':
      case 'day': {
        const d = new Date(view.year, (view.month ?? 1) - 1, (view.day ?? 1));
        d.setDate(d.getDate() - (view.scale === 'week' ? 7 : 1));
        onNavigate({ ...view, year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate() });
        break;
      }
    }
  }

  function next() {
    switch (view.scale) {
      case 'year':
        onNavigate({ ...view, year: view.year + 1 });
        break;
      case 'month':
        if (view.month === 12) onNavigate({ ...view, year: view.year + 1, month: 1 });
        else onNavigate({ ...view, month: (view.month ?? 1) + 1 });
        break;
      case 'week':
      case 'day': {
        const d = new Date(view.year, (view.month ?? 1) - 1, (view.day ?? 1));
        d.setDate(d.getDate() + (view.scale === 'week' ? 7 : 1));
        onNavigate({ ...view, year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate() });
        break;
      }
    }
  }

  const breadcrumbs: { label: string; onClick: () => void }[] = [
    {
      label: String(view.year),
      onClick: () => onNavigate({ scale: 'year', year: view.year }),
    },
  ];

  if (view.month) {
    breadcrumbs.push({
      label: MONTH_NAMES[view.month - 1],
      onClick: () => onNavigate({ scale: 'month', year: view.year, month: view.month }),
    });
  }

  if (view.day) {
    breadcrumbs.push({
      label: String(view.day),
      onClick: () => onNavigate({ scale: 'day', year: view.year, month: view.month, day: view.day }),
    });
  }

  return (
    <nav className="time-nav" aria-label="Time navigation">
      <button
        type="button"
        className={`date-chip ${expanded ? 'open' : ''}`}
        onClick={() => setExpanded(e => !e)}
        aria-expanded={expanded}
      >
        <span>{summaryLabel(view, selectedHourRange)}</span>
        <span className="date-chip-icon" aria-hidden="true">&#9662;</span>
      </button>

      <div className={`date-panel ${expanded ? 'open' : ''}`}>
        <div className="time-nav-scales" role="tablist">
          {SCALES.map(s => (
            <button
              key={s}
              role="tab"
              aria-selected={view.scale === s}
              className={`scale-btn ${view.scale === s ? 'active' : ''}`}
              onClick={() => {
                const newView: ViewState = { scale: s, year: view.year };
                if (s !== 'year') newView.month = view.month ?? now.getMonth() + 1;
                if (s === 'day' || s === 'week') newView.day = view.day ?? now.getDate();
                onNavigate(newView);
              }}
            >
              {s.charAt(0).toUpperCase() + s.slice(1)}
            </button>
          ))}
        </div>

        <GranularitySelect
          value={granularity}
          onChange={onGranularityChange}
          rangeMs={rangeMs}
          minBucketMs={minBucketMs}
        />

        <div className="time-nav-controls">
          <div className="date-stepper">
            <button className="nav-btn step-btn" onClick={prev} aria-label="Previous">&lsaquo;</button>

            <ol className="breadcrumbs" aria-label="Current time position">
              {breadcrumbs.map((b, i) => (
                <li key={i}>
                  {i < breadcrumbs.length - 1
                    ? <button className="breadcrumb-link" onClick={b.onClick}>{b.label}</button>
                    : <span className="breadcrumb-current" aria-current="page">{b.label}</span>}
                </li>
              ))}
            </ol>

            <button className="nav-btn step-btn" onClick={next} aria-label="Next">&rsaquo;</button>
          </div>

          <div className="time-nav-secondary">
            <button
              className="nav-btn today-btn"
              onClick={() => onNavigate({
                scale: 'day',
                year: now.getFullYear(),
                month: now.getMonth() + 1,
                day: now.getDate(),
              })}
            >
              Today
            </button>

            <JumpToTimestamp onJump={(ts) => {
              const d = new Date(ts);
              onNavigate({
                scale: 'day',
                year: d.getFullYear(),
                month: d.getMonth() + 1,
                day: d.getDate(),
              });
            }} />
          </div>
        </div>

        {view.scale === 'year' && (
          <YearGrid
            year={view.year}
            onSelectMonth={(m) => onNavigate({ scale: 'month', year: view.year, month: m })}
            minTs={minTs}
            maxTs={maxTs}
          />
        )}
        {view.scale === 'month' && view.month && (
          <MonthGrid
            year={view.year}
            month={view.month}
            selectedDay={view.day}
            onSelectDay={(d) => onNavigate({ scale: 'day', year: view.year, month: view.month, day: d })}
            minTs={minTs}
            maxTs={maxTs}
          />
        )}
        {view.scale === 'day' && view.month && view.day && onHourSelect && (
          <HourGrid
            year={view.year}
            month={view.month}
            day={view.day}
            selectedRange={selectedHourRange ?? null}
            onSelectRange={(range) => {
              if (range === null) {
                onHourSelect(null);
              } else {
                const start = new Date(view.year, view.month! - 1, view.day!, range.startHour);
                const end = new Date(view.year, view.month! - 1, view.day!, range.endHour + 1);
                onHourSelect({
                  from: start.getTime(),
                  to: end.getTime() - 1,
                  startHour: range.startHour,
                  endHour: range.endHour,
                });
              }
            }}
            minTs={minTs}
            maxTs={maxTs}
          />
        )}
      </div>
    </nav>
  );
}

function YearGrid({ year, onSelectMonth, minTs, maxTs }: {
  year: number;
  onSelectMonth: (month: number) => void;
  minTs: number | null;
  maxTs: number | null;
}) {
  return (
    <div className="year-grid" role="grid" aria-label={`Months of ${year}`}>
      {MONTH_NAMES.map((name, i) => {
        const monthStart = new Date(year, i, 1).getTime();
        const monthEnd = new Date(year, i + 1, 1).getTime() - 1;
        const hasData = minTs !== null && maxTs !== null && monthEnd >= minTs && monthStart <= maxTs;
        return (
          <button
            key={i}
            className={`month-cell ${hasData ? 'has-data' : 'no-data'}`}
            onClick={() => onSelectMonth(i + 1)}
            aria-label={`${name} ${year}${hasData ? '' : ' (no data)'}`}
          >
            {name}
          </button>
        );
      })}
    </div>
  );
}

function MonthGrid({ year, month, selectedDay, onSelectDay, minTs, maxTs }: {
  year: number;
  month: number;
  selectedDay?: number;
  onSelectDay: (day: number) => void;
  minTs: number | null;
  maxTs: number | null;
}) {
  const days = getDaysInMonth(year, month);
  const now = new Date();
  const isCurrentMonth = year === now.getFullYear() && month === now.getMonth() + 1;

  return (
    <div className="nav-subgrid-section">
      <span className="nav-subgrid-label">Jump to day</span>
      <div className="month-grid" role="grid" aria-label={`Days of ${MONTH_NAMES[month - 1]} ${year}`}>
        {Array.from({ length: days }, (_, i) => {
          const day = i + 1;
          const dayStart = new Date(year, month - 1, day).getTime();
          const dayEnd = new Date(year, month - 1, day + 1).getTime() - 1;
          const hasData = minTs !== null && maxTs !== null && dayEnd >= minTs && dayStart <= maxTs;
          const isToday = isCurrentMonth && day === now.getDate();
          const isSelected = day === selectedDay;
          return (
            <button
              key={day}
              className={`day-cell ${hasData ? 'has-data' : 'no-data'} ${isToday ? 'today' : ''} ${isSelected ? 'selected' : ''}`}
              onClick={() => onSelectDay(day)}
              aria-label={`${day} ${MONTH_NAMES[month - 1]}${hasData ? '' : ' (no data)'}${isToday ? ' (today)' : ''}`}
            >
              {day}
            </button>
          );
        })}
      </div>
    </div>
  );
}

function HourGrid({ year, month, day, selectedRange, onSelectRange, minTs, maxTs }: {
  year: number;
  month: number;
  day: number;
  selectedRange: HourRange | null;
  onSelectRange: (range: HourRange | null) => void;
  minTs: number | null;
  maxTs: number | null;
}) {
  // The anchor hour is local UI state (which hour a shift-click range extends from);
  // it resets whenever the displayed day changes so a stale anchor can't leak across days.
  const [anchorHour, setAnchorHour] = useState<number | null>(null);

  useEffect(() => {
    setAnchorHour(null);
  }, [year, month, day]);

  function handleHourClick(hour: number, shiftKey: boolean) {
    if (shiftKey && anchorHour !== null) {
      onSelectRange({ startHour: Math.min(anchorHour, hour), endHour: Math.max(anchorHour, hour) });
    } else {
      setAnchorHour(hour);
      onSelectRange({ startHour: hour, endHour: hour });
    }
  }

  return (
    <div className="nav-subgrid-section">
      <div className="hour-filter-header">
        <span className="nav-subgrid-label">Filter by hour</span>
        <button
          type="button"
          className={`hour-reset-btn ${selectedRange === null ? 'active' : ''}`}
          onClick={() => { setAnchorHour(null); onSelectRange(null); }}
          aria-pressed={selectedRange === null}
        >
          All hours
        </button>
      </div>
      <div className="hour-grid" role="grid" aria-label={`Hours of ${day} ${MONTH_NAMES[month - 1]} ${year}`}>
        {Array.from({ length: 24 }, (_, hour) => {
          const hourStart = new Date(year, month - 1, day, hour).getTime();
          const hourEnd = new Date(year, month - 1, day, hour + 1).getTime() - 1;
          const hasData = minTs !== null && maxTs !== null && hourEnd >= minTs && hourStart <= maxTs;
          const inRange = selectedRange !== null && hour >= selectedRange.startHour && hour <= selectedRange.endHour;
          const isRangeStart = inRange && hour === selectedRange!.startHour;
          const isRangeEnd = inRange && hour === selectedRange!.endHour;
          // Suppress the 6-hour group gap where a selected range continues across the boundary.
          const isGroupEnd = hour % 6 === 5 && !(inRange && !isRangeEnd);
          const label = pad2(hour);
          const className = [
            'hour-cell',
            hasData ? 'has-data' : 'no-data',
            inRange ? 'selected' : '',
            isRangeStart ? 'range-start' : '',
            isRangeEnd ? 'range-end' : '',
            isGroupEnd ? 'group-end' : '',
          ].filter(Boolean).join(' ');
          return (
            <button
              key={hour}
              type="button"
              className={className}
              onClick={(e) => handleHourClick(hour, e.shiftKey)}
              aria-label={`${label}:00${hasData ? '' : ' (no data)'}`}
              aria-pressed={inRange}
            >
              {label}
            </button>
          );
        })}
      </div>
      <p className="hour-range-hint">Shift-click another hour to select a range.</p>
    </div>
  );
}

function JumpToTimestamp({ onJump }: { onJump: (ts: number) => void }) {
  function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const input = (e.currentTarget.elements.namedItem('timestamp') as HTMLInputElement)?.value;
    if (!input) return;
    const ts = new Date(input).getTime();
    if (!isNaN(ts)) onJump(ts);
  }

  return (
    <form className="jump-form" onSubmit={handleSubmit} aria-label="Jump to date">
      <input
        type="date"
        name="timestamp"
        aria-label="Jump to date"
      />
      <button type="submit" className="nav-btn">Go</button>
    </form>
  );
}
