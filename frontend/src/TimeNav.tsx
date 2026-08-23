import type { ViewState, ViewScale } from './types';
import { getDaysInMonth } from './utils';

interface TimeNavProps {
  view: ViewState;
  onNavigate: (view: ViewState) => void;
  onHourSelect?: (from: number, to: number) => void;
  selectedHour?: number | null;
  minTs: number | null;
  maxTs: number | null;
}

const SCALES: ViewScale[] = ['year', 'month', 'week', 'day'];
const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function formatHourLabel(hour: number): string {
  if (hour === 0) return '12a';
  if (hour < 12) return `${hour}a`;
  if (hour === 12) return '12p';
  return `${hour - 12}p`;
}

export function TimeNav({ view, onNavigate, onHourSelect, selectedHour, minTs, maxTs }: TimeNavProps) {
  const now = new Date();

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

      <div className="time-nav-controls">
        <button className="nav-btn" onClick={prev} aria-label="Previous">&lsaquo;</button>

        <ol className="breadcrumbs" aria-label="Current time position">
          {breadcrumbs.map((b, i) => (
            <li key={i}>
              {i < breadcrumbs.length - 1
                ? <button className="breadcrumb-link" onClick={b.onClick}>{b.label}</button>
                : <span className="breadcrumb-current" aria-current="page">{b.label}</span>}
            </li>
          ))}
        </ol>

        <button className="nav-btn" onClick={next} aria-label="Next">&rsaquo;</button>

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
          selectedHour={selectedHour ?? null}
          onSelectHour={(hour) => {
            if (hour === null) {
              onHourSelect(0, 0);
            } else {
              const start = new Date(view.year, view.month! - 1, view.day!, hour);
              const end = new Date(view.year, view.month! - 1, view.day!, hour + 1);
              onHourSelect(start.getTime(), end.getTime() - 1);
            }
          }}
          minTs={minTs}
          maxTs={maxTs}
        />
      )}
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

function HourGrid({ year, month, day, selectedHour, onSelectHour, minTs, maxTs }: {
  year: number;
  month: number;
  day: number;
  selectedHour: number | null;
  onSelectHour: (hour: number | null) => void;
  minTs: number | null;
  maxTs: number | null;
}) {
  return (
    <div className="nav-subgrid-section">
      <span className="nav-subgrid-label">Filter by hour</span>
      <div className="hour-grid" role="grid" aria-label={`Hours of ${day} ${MONTH_NAMES[month - 1]} ${year}`}>
        <button
          className={`hour-cell ${selectedHour === null ? 'selected' : ''}`}
          onClick={() => onSelectHour(null)}
          aria-label="Show full day"
          aria-pressed={selectedHour === null}
        >
          All
        </button>
        {Array.from({ length: 24 }, (_, hour) => {
          const hourStart = new Date(year, month - 1, day, hour).getTime();
          const hourEnd = new Date(year, month - 1, day, hour + 1).getTime() - 1;
          const hasData = minTs !== null && maxTs !== null && hourEnd >= minTs && hourStart <= maxTs;
          return (
            <button
              key={hour}
              className={`hour-cell ${hasData ? 'has-data' : 'no-data'} ${selectedHour === hour ? 'selected' : ''}`}
              onClick={() => onSelectHour(hour)}
              aria-label={`${formatHourLabel(hour)}${hasData ? '' : ' (no data)'}`}
              aria-pressed={selectedHour === hour}
            >
              {formatHourLabel(hour)}
            </button>
          );
        })}
      </div>
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
