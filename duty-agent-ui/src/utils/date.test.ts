import { describe, it, expect } from 'vitest';
import {
  formatLocalDate,
  startOfWeek,
  isWorkday,
  weekdayShort,
  monthDayLabel,
  personsOf,
  hasDuty,
} from './date';

describe('formatLocalDate', () => {
  it('本地年月日补零', () => {
    expect(formatLocalDate(new Date(2026, 8, 4))).toBe('2026-09-04');
  });
});

describe('startOfWeek', () => {
  it('周三回退到周一', () => {
    const got = startOfWeek(new Date(2026, 8, 23));
    expect(formatLocalDate(got)).toBe('2026-09-21');
  });

  it('周日归到本周一（周一起点）', () => {
    const got = startOfWeek(new Date(2026, 8, 27));
    expect(formatLocalDate(got)).toBe('2026-09-21');
  });

  it('不修改入参', () => {
    const src = new Date(2026, 8, 23);
    startOfWeek(src);
    expect(formatLocalDate(src)).toBe('2026-09-23');
  });
});

describe('isWorkday', () => {
  it('周六日为假，周三为真', () => {
    expect(isWorkday(new Date(2026, 8, 26))).toBe(false);
    expect(isWorkday(new Date(2026, 8, 27))).toBe(false);
    expect(isWorkday(new Date(2026, 8, 23))).toBe(true);
  });
});

describe('labels', () => {
  it('weekdayShort/monthDayLabel', () => {
    expect(weekdayShort('2026-09-25')).toBe('周五');
    expect(monthDayLabel('2026-09-04')).toBe('9/4');
  });
});

describe('personsOf / hasDuty', () => {
  it('多区域去重', () => {
    expect(
      personsOf({ date: '2026-09-23', area_assignments: { A: ['甲', '乙'], B: ['乙', '丙'] } } as never),
    ).toEqual(['甲', '乙', '丙']);
  });

  it('undefined/空安排无值班', () => {
    expect(personsOf(undefined)).toEqual([]);
    expect(hasDuty(undefined)).toBe(false);
    expect(hasDuty({ date: '2026-09-23', area_assignments: {} } as never)).toBe(false);
  });
});
