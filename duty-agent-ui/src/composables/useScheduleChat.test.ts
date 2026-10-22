import { describe, it, expect } from 'vitest';
import { warningsOf } from './useScheduleChat';

describe('warningsOf', () => {
  it('读取 single_pass 的 adjustment_warnings', () => {
    expect(
      warningsOf({
        status: 'success',
        adjustment_warnings: ['10-22/教室: 剔除缺席人员 1001', '未覆盖日期 10-25'],
      }),
    ).toEqual(['10-22/教室: 剔除缺席人员 1001', '未覆盖日期 10-25']);
  });

  it('读取 multi_agent 的 agent_summary.warnings', () => {
    expect(
      warningsOf({
        status: 'success',
        agent_summary: { warnings: ['2026-10-22: 模型未安排配置区域「教室」'] },
      }),
    ).toEqual(['2026-10-22: 模型未安排配置区域「教室」']);
  });

  it('两种来源同时存在时都收集', () => {
    expect(
      warningsOf({
        adjustment_warnings: ['a'],
        agent_summary: { warnings: ['b'] },
      }),
    ).toEqual(['a', 'b']);
  });

  it('过滤空白项', () => {
    expect(warningsOf({ adjustment_warnings: ['a', '', '   ', 'b'] })).toEqual(['a', 'b']);
  });

  it('没有告警时返回空数组', () => {
    expect(warningsOf({ status: 'success' })).toEqual([]);
    expect(warningsOf({ adjustment_warnings: 'not-a-list' })).toEqual([]);
    expect(warningsOf({ agent_summary: { warnings: 'nope' } })).toEqual([]);
  });
});
