/**
 * 日期与排班口径的统一工具。
 * 周起点一律周一(weekStartsOn = 1);"有排班"一律看 personsOf(entry).length > 0;
 * 工作日判断一律 isWorkday。仪表盘 / 日历 / AI 排班页三处共用,不允许页面自算。
 */
import type { ScheduleEntry } from '@/types';

export function formatLocalDate(date = new Date()): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** fmtDate 是短别名,业务代码统一用它 */
export const fmtDate = formatLocalDate;

export function compareDateStrings(a: string, b: string): number {
  return a.localeCompare(b);
}

/** 所在周的起点(默认周一)。返回新 Date,不修改入参。 */
export function startOfWeek(date: Date, weekStartsOn: 0 | 1 = 1): Date {
  const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const diff = (d.getDay() - weekStartsOn + 7) % 7;
  d.setDate(d.getDate() - diff);
  return d;
}

export function isWorkday(date: Date): boolean {
  const day = date.getDay();
  return day !== 0 && day !== 6;
}

export function weekdayShort(iso: string): string {
  const d = new Date(`${iso}T00:00:00`);
  return `周${'日一二三四五六'[d.getDay()]}`;
}

/** "9/4" 形式的短日期 */
export function monthDayLabel(iso: string): string {
  const d = new Date(`${iso}T00:00:00`);
  return `${d.getMonth() + 1}/${d.getDate()}`;
}

/** "9月4日 · 周五" 形式,给 Hero/标题用;ISO 留给表格 */
export function cnDateLabel(iso: string): string {
  const d = new Date(`${iso}T00:00:00`);
  return `${d.getMonth() + 1} 月 ${d.getDate()} 日 · ${weekdayShort(iso)}`;
}

/** 展开区域安排为去重人名列表 */
export function personsOf(entry: ScheduleEntry | undefined): string[] {
  if (!entry) return [];
  return [...new Set(Object.values(entry.area_assignments ?? {}).flat())];
}

/** 该日期是否有实际排班(area_assignments 为空对象/空数组不算) */
export function hasDuty(entry: ScheduleEntry | undefined): boolean {
  return personsOf(entry).length > 0;
}
