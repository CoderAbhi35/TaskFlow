import { formatDuration, formatPercent, formatRelative, humanize, shortId } from './format';

describe('formatDuration', () => {
  it.each([
    [null, '—'],
    [850, '850 ms'],
    [1234, '1.23 s'],
    [12_300, '12.3 s'],
    [125_000, '2m 5s'],
    [7_500_000, '2h 5m'],
  ])('%s ms → %s', (ms, expected) => {
    expect(formatDuration(ms)).toBe(expected);
  });
});

describe('formatRelative', () => {
  const now = Date.parse('2026-09-24T12:00:00Z');

  it.each([
    ['2026-09-24T11:59:58Z', 'just now'],
    ['2026-09-24T11:59:18Z', '42 s ago'],
    ['2026-09-24T11:55:00Z', '5 min ago'],
    ['2026-09-24T15:00:00Z', 'in 3 h'],
  ])('%s → %s', (iso, expected) => {
    expect(formatRelative(iso, now)).toBe(expected);
  });

  it('shows a dash for missing times', () => {
    expect(formatRelative(null, now)).toBe('—');
  });
});

describe('formatPercent', () => {
  it.each([
    [null, '—'],
    [1, '100%'],
    [0, '0%'],
    [0.9876, '98.8%'],
  ])('%s → %s', (ratio, expected) => {
    expect(formatPercent(ratio)).toBe(expected);
  });
});

describe('shortId', () => {
  it('uses the random tail of a UUIDv7, not the shared timestamp prefix', () => {
    const a = '01a0d464-1111-7000-8000-00000000aaaa';
    const b = '01a0d464-2222-7000-8000-00000000bbbb';
    expect(shortId(a)).not.toBe(shortId(b));
    expect(shortId(a)).toBe('0000aaaa');
  });
});

describe('humanize', () => {
  it('turns wire enums into labels', () => {
    expect(humanize('DEAD_LETTERED')).toBe('Dead lettered');
    expect(humanize('HIGH')).toBe('High');
  });
});
