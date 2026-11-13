import { describe, it, expect } from 'vitest';
import {
  formatLocalDate,
  startOfWeek,
  isWorkday,
  weekdayShort,
  monthDayLabel,
  parseDisplayBoundary,
  resolveDisplayDutyDate,
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

describe('resolveDisplayDutyDate（值日预览纯规则）', () => {
  // JS 月份从 0 开始：10 = 11 月。
  const at = (day: number, hour: number, minute = 0) => new Date(2026, 10, day, hour, minute);

  it('边界前显示今天，到点切到次日', () => {
    expect(resolveDisplayDutyDate(at(11, 7, 59), '08:00', true)).toBe('2026-11-11');
    expect(resolveDisplayDutyDate(at(11, 8, 0), '08:00', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(11, 23, 59), '08:00', true)).toBe('2026-11-12');
  });

  it('跨午夜连续：同一日期只是标签从明日变今日', () => {
    expect(resolveDisplayDutyDate(at(12, 0, 0), '08:00', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(12, 7, 59), '08:00', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(12, 8, 0), '08:00', true)).toBe('2026-11-13');
  });

  it('关闭预览后始终显示设备日历今天', () => {
    for (const hour of [0, 8, 12, 23]) {
      expect(resolveDisplayDutyDate(at(11, hour), '08:00', false)).toBe('2026-11-11');
    }
  });

  it('非法边界回退 08:00，00:00 / 23:59 也能正确工作', () => {
    expect(resolveDisplayDutyDate(at(11, 12), 'not-a-time', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(11, 12), '99:99', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(11, 0), '00:00', true)).toBe('2026-11-12');
    expect(resolveDisplayDutyDate(at(11, 23, 58), '23:59', true)).toBe('2026-11-11');
    expect(resolveDisplayDutyDate(at(11, 23, 59), '23:59', true)).toBe('2026-11-12');
    expect(parseDisplayBoundary('18:30')).toEqual([18, 30]);
    expect(parseDisplayBoundary('garbage')).toEqual([8, 0]);
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
