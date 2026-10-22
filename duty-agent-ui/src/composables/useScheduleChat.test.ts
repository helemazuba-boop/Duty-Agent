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

describe('reasoning 契约', () => {
  // 后端把思考走 stream_reasoning phase 下发，与正文流（stream_chunk）同级但
  // 绝不混流；结束后 schedule_complete 带完整 reasoning_content 覆盖增量。
  // 这里钉死前端对这两种形态的读取方式。
  it('从 schedule_complete 读取 reasoning_content', () => {
    const msg = {
      type: 'schedule_complete',
      status: 'success',
      ai_response: '[areas]\nA = 教室',
      reasoning_content: '先看谁在请假…',
    };
    const reasoning =
      typeof msg.reasoning_content === 'string' ? msg.reasoning_content.trim() : '';
    expect(reasoning).toBe('先看谁在请假…');
  });

  it('没有思考时不产生空区块', () => {
    for (const msg of [
      { status: 'success' },
      { status: 'success', reasoning_content: '' },
      { status: 'success', reasoning_content: '   ' },
      { status: 'success', reasoning_content: 42 },
    ]) {
      const reasoning =
        typeof msg.reasoning_content === 'string' ? msg.reasoning_content.trim() : '';
      expect(reasoning).toBe('');
    }
  });

  it('代码兜底没有思考', () => {
    // completion_source=fallback 时后端 reset_last_reasoning()，不下发该键。
    const msg: { status: string; completion_source?: string; ai_response?: string; reasoning_content?: unknown } = {
      status: 'success',
      completion_source: 'fallback',
      ai_response: 'fallback_fill',
    };
    const reasoning =
      typeof msg.reasoning_content === 'string' ? msg.reasoning_content.trim() : '';
    expect(reasoning).toBe('');
  });
});
