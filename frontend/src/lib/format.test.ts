import { describe, expect, it } from 'vitest';
import {
  dayBoundary,
  describeSkipReason,
  describeUserAgent,
  formatBytes,
  formatDate,
  formatDays,
  formatDuration,
  formatPercent,
  formatRelative,
  humanize,
  isValidTime,
  normalizeKb,
  truncate,
} from './format';
import { aggregateTrend } from './trend';

describe('normalizeKb', () => {
  it('accepts KB numbers with or without the prefix, any case, surrounding spaces', () => {
    expect(normalizeKb('KB5030219')).toBe('KB5030219');
    expect(normalizeKb('kb5030219')).toBe('KB5030219');
    expect(normalizeKb(' 5030219 ')).toBe('KB5030219');
    expect(normalizeKb('1')).toBe('KB1');
  });
  it('rejects anything else', () => {
    expect(normalizeKb('')).toBeNull();
    expect(normalizeKb('KB')).toBeNull();
    expect(normalizeKb('KB12345678901')).toBeNull();
    expect(normalizeKb('KB50-30219')).toBeNull();
    expect(normalizeKb('Q5030219')).toBeNull();
  });
});

describe('isValidTime', () => {
  it('accepts 24 h HH:mm', () => {
    expect(isValidTime('00:00')).toBe(true);
    expect(isValidTime('02:30')).toBe(true);
    expect(isValidTime('23:59')).toBe(true);
  });
  it('rejects malformed times', () => {
    for (const v of ['24:00', '2:30', '02:60', '0230', '', '02:30:00']) expect(isValidTime(v)).toBe(false);
  });
});

describe('formatting', () => {
  it('formats bytes', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(null)).toBe('—');
  });
  it('formats percentages and durations', () => {
    expect(formatPercent(87.4)).toBe('87%');
    expect(formatPercent(null)).toBe('—');
    expect(formatDuration(180)).toBe('3 h');
    expect(formatDuration(90)).toBe('1 h 30 min');
    expect(formatDuration(45)).toBe('45 min');
    expect(formatDuration(1440)).toBe('1 day');
    expect(formatDuration(10080)).toBe('7 days');
    expect(formatDuration(1500)).toBe('25 h');
  });
  it('humanizes enum values', () => {
    expect(humanize('WaitingForReboot')).toBe('Waiting for restart');
    expect(humanize('DotNet')).toBe('.NET');
    expect(humanize('ServicingStack')).toBe('Servicing stack');
    expect(humanize(null)).toBe('—');
  });
  it('formats maintenance days', () => {
    expect(formatDays([0, 6])).toContain('Sun');
    expect(formatDays([])).toBe('No days');
  });
  it('keeps date-only values on their calendar day in every time zone', () => {
    expect(formatDate('2026-11-13')).toMatch(/13/);
  });
  it('formats relative times', () => {
    const now = Date.parse('2026-11-13T12:00:00Z');
    expect(formatRelative('2026-11-13T11:55:00Z', now)).toMatch(/5/);
    expect(formatRelative('2026-11-13T11:59:50Z', now)).toBe('just now');
    expect(formatRelative(null, now)).toBe('never');
  });
  it('truncates with an ellipsis', () => {
    expect(truncate('abcdef', 4)).toBe('abc…');
    expect(truncate('abc', 4)).toBe('abc');
  });
  it('explains skip reasons', () => {
    expect(describeSkipReason('CategoryNotAllowed')).toBe('Category not allowed by policy');
    expect(describeSkipReason('SomethingNew')).toBe('SomethingNew');
    expect(describeSkipReason(null)).toBe('');
  });
  it('describes user agents', () => {
    expect(describeUserAgent('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0')).toBe(
      'Edge on Windows',
    );
    expect(describeUserAgent('Mozilla/5.0 (Macintosh; Intel Mac OS X 15_1) Gecko/20100101 Firefox/144.0')).toBe('Firefox on macOS');
    expect(describeUserAgent(null)).toBe('Unknown device');
  });
});

describe('dayBoundary', () => {
  it('covers the whole local day', () => {
    const start = new Date(dayBoundary('2026-11-13', false)!);
    const end = new Date(dayBoundary('2026-11-13', true)!);
    expect(start.getHours()).toBe(0);
    expect(start.getDate()).toBe(13);
    expect(end.getHours()).toBe(23);
    expect(end.getMilliseconds()).toBe(999);
    expect(end.getTime() - start.getTime()).toBeGreaterThan(22 * 3600_000);
  });
  it('ignores empty or malformed input', () => {
    expect(dayBoundary('', true)).toBeUndefined();
    expect(dayBoundary('garbage', false)).toBeUndefined();
  });
});

describe('aggregateTrend', () => {
  const point = (date: string, total: number, compliant: number) => ({
    date,
    totalEndpoints: total,
    compliant,
    missingUpdates: total - compliant,
    failedUpdates: 0,
    rebootRequired: 0,
    scanStale: 0,
    neverScanned: 0,
    offline: 0,
    missingRequiredUpdates: 0,
    compliancePercent: total ? (compliant / total) * 100 : 0,
  });
  it('merges per-organization points by date and recomputes the percentage', () => {
    const out = aggregateTrend([point('2026-11-02', 10, 10), point('2026-11-01', 4, 1), point('2026-11-02', 30, 10)]);
    expect(out.map((p) => p.date)).toEqual(['2026-11-01', '2026-11-02']);
    expect(out[1].totalEndpoints).toBe(40);
    expect(out[1].compliant).toBe(20);
    expect(out[1].compliancePercent).toBe(50);
  });
});
