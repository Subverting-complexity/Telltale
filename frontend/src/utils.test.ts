import { describe, it, expect } from 'vitest';
import {
  formatSize, formatElapsed, formatCpu, formatIo, formatDate, formatTime,
  getDaysInMonth, getDayRange, getMonthRange, getYearRange, getWeekRange, clamp,
  categoriseProcess, formatRate, formatMemoryPercent, formatSizeGb,
} from './utils';

describe('formatSize', () => {
  it('formats KB', () => {
    expect(formatSize(0.5)).toBe('512 KB');
  });
  it('formats MB', () => {
    expect(formatSize(256)).toBe('256.0 MB');
  });
  it('formats GB', () => {
    expect(formatSize(2048)).toBe('2.0 GB');
  });
  it('formats small KB', () => {
    expect(formatSize(0.001)).toBe('1 KB');
  });
  it('formats exact GB boundary', () => {
    expect(formatSize(1024)).toBe('1.0 GB');
  });
});

describe('formatElapsed', () => {
  it('formats seconds', () => {
    expect(formatElapsed(5000)).toBe('5s');
  });
  it('formats minutes', () => {
    expect(formatElapsed(90000)).toBe('1m 30s');
  });
  it('formats hours', () => {
    expect(formatElapsed(3700000)).toBe('1h 1m');
  });
  it('formats days', () => {
    expect(formatElapsed(90000000)).toBe('1d 1h');
  });
  it('formats zero', () => {
    expect(formatElapsed(0)).toBe('0s');
  });
  it('formats exact minute', () => {
    expect(formatElapsed(60000)).toBe('1m 0s');
  });
});

describe('formatCpu', () => {
  it('handles null', () => {
    expect(formatCpu(null)).toBe('-');
  });
  it('formats small values with two decimals', () => {
    expect(formatCpu(0.12)).toBe('0.12%');
  });
  it('formats medium values with one decimal', () => {
    expect(formatCpu(25.5)).toBe('25.5%');
  });
  it('formats large values as integers', () => {
    expect(formatCpu(150)).toBe('150%');
  });
  it('formats zero', () => {
    expect(formatCpu(0)).toBe('0.00%');
  });
  it('formats values just below 10', () => {
    expect(formatCpu(9.99)).toBe('9.99%');
  });
  it('formats values at 10', () => {
    expect(formatCpu(10)).toBe('10.0%');
  });
});

describe('formatIo', () => {
  it('handles null', () => {
    expect(formatIo(null)).toBe('-');
  });
  it('formats KB', () => {
    expect(formatIo(500)).toBe('500 KB');
  });
  it('formats MB', () => {
    expect(formatIo(2048)).toBe('2.0 MB');
  });
  it('formats GB', () => {
    expect(formatIo(2097152)).toBe('2.0 GB');
  });
  it('formats zero', () => {
    expect(formatIo(0)).toBe('0 KB');
  });
});

describe('formatDate', () => {
  it('returns a non-empty string', () => {
    const result = formatDate(1692806400000);
    expect(result.length).toBeGreaterThan(0);
  });
});

describe('formatTime', () => {
  it('returns a non-empty string', () => {
    const result = formatTime(1692806400000);
    expect(result.length).toBeGreaterThan(0);
  });
});

describe('getDaysInMonth', () => {
  it('returns 31 for January', () => {
    expect(getDaysInMonth(2026, 1)).toBe(31);
  });
  it('returns 28 for February non-leap', () => {
    expect(getDaysInMonth(2025, 2)).toBe(28);
  });
  it('returns 29 for February leap', () => {
    expect(getDaysInMonth(2024, 2)).toBe(29);
  });
  it('returns 30 for April', () => {
    expect(getDaysInMonth(2026, 4)).toBe(30);
  });
  it('returns 31 for December', () => {
    expect(getDaysInMonth(2026, 12)).toBe(31);
  });
});

describe('getDayRange', () => {
  it('returns start of day to end of day', () => {
    const { from, to } = getDayRange(2026, 8, 23);
    const start = new Date(from);
    expect(start.getFullYear()).toBe(2026);
    expect(start.getMonth()).toBe(7);
    expect(start.getDate()).toBe(23);
    expect(to).toBeGreaterThan(from);
    expect(to - from).toBeLessThan(86400001);
  });
});

describe('getMonthRange', () => {
  it('covers the full month', () => {
    const { from, to } = getMonthRange(2026, 8);
    const start = new Date(from);
    const end = new Date(to + 1);
    expect(start.getMonth()).toBe(7);
    expect(end.getMonth()).toBe(8);
  });
});

describe('getYearRange', () => {
  it('covers the full year', () => {
    const { from, to } = getYearRange(2026);
    const start = new Date(from);
    const end = new Date(to + 1);
    expect(start.getFullYear()).toBe(2026);
    expect(start.getMonth()).toBe(0);
    expect(end.getFullYear()).toBe(2027);
  });
});

describe('getWeekRange', () => {
  it('returns a 7-day range', () => {
    const { from, to } = getWeekRange(2026, 8, 23);
    const days = (to - from) / 86400000;
    expect(days).toBeCloseTo(7, 0);
  });
});

describe('clamp', () => {
  it('clamps below min', () => {
    expect(clamp(-5, 0, 100)).toBe(0);
  });
  it('clamps above max', () => {
    expect(clamp(150, 0, 100)).toBe(100);
  });
  it('returns value in range', () => {
    expect(clamp(50, 0, 100)).toBe(50);
  });
  it('handles min equals max', () => {
    expect(clamp(50, 10, 10)).toBe(10);
  });
  it('handles value at boundary', () => {
    expect(clamp(0, 0, 100)).toBe(0);
    expect(clamp(100, 0, 100)).toBe(100);
  });
});

describe('categoriseProcess', () => {
  it('identifies system processes by name', () => {
    expect(categoriseProcess('csrss.exe', null)).toBe('system');
    expect(categoriseProcess('System', null)).toBe('system');
    expect(categoriseProcess('Idle', null)).toBe('system');
    expect(categoriseProcess('svchost.exe', null)).toBe('system');
  });

  it('is case-insensitive for names', () => {
    expect(categoriseProcess('CSRSS.EXE', null)).toBe('system');
    expect(categoriseProcess('Svchost.exe', null)).toBe('system');
  });

  it('identifies system processes by path', () => {
    expect(categoriseProcess('unknown.exe', 'C:\\Windows\\System32\\unknown.exe')).toBe('system');
    expect(categoriseProcess('unknown.exe', 'C:\\Windows\\SysWOW64\\unknown.exe')).toBe('system');
  });

  it('identifies services by path', () => {
    expect(categoriseProcess('myapp.exe', 'C:\\Program Files\\MyApp\\myapp.exe')).toBe('services');
    expect(categoriseProcess('myapp.exe', 'C:\\Program Files (x86)\\MyApp\\myapp.exe')).toBe('services');
  });

  it('categorises unknown processes as applications', () => {
    expect(categoriseProcess('chrome.exe', 'D:\\Apps\\chrome.exe')).toBe('applications');
    expect(categoriseProcess('myapp.exe', null)).toBe('applications');
  });

  it('prefers name match over path', () => {
    expect(categoriseProcess('svchost.exe', 'D:\\somewhere\\svchost.exe')).toBe('system');
  });
});

describe('formatRate', () => {
  it('handles null', () => {
    expect(formatRate(null)).toBe('-');
  });
  it('formats KB/s', () => {
    expect(formatRate(500)).toBe('500 KB/s');
  });
  it('formats MB/s', () => {
    expect(formatRate(2048)).toBe('2.0 MB/s');
  });
  it('formats GB/s', () => {
    expect(formatRate(2097152)).toBe('2.0 GB/s');
  });
  it('formats zero', () => {
    expect(formatRate(0)).toBe('0 KB/s');
  });
});

describe('formatMemoryPercent', () => {
  it('handles null available', () => {
    expect(formatMemoryPercent(null, 16384)).toBe('-');
  });
  it('handles null total', () => {
    expect(formatMemoryPercent(8192, null)).toBe('-');
  });
  it('handles zero total', () => {
    expect(formatMemoryPercent(8192, 0)).toBe('-');
  });
  it('calculates used percentage', () => {
    expect(formatMemoryPercent(8192, 16384)).toBe('50.0%');
  });
  it('handles high usage', () => {
    expect(formatMemoryPercent(1638, 16384)).toBe('90.0%');
  });
});

describe('formatSizeGb', () => {
  it('formats MB', () => {
    expect(formatSizeGb(512)).toBe('512 MB');
  });
  it('formats GB', () => {
    expect(formatSizeGb(2048)).toBe('2.0 GB');
  });
  it('formats exact GB boundary', () => {
    expect(formatSizeGb(1024)).toBe('1.0 GB');
  });
});
