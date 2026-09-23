import { describe, it, expect } from 'vitest';
import { splitSSEChunk, parseEventName, parseEventData, safeParse } from './sseParser';

describe('splitSSEChunk', () => {
  it('拆出行并保留尾部残段', () => {
    expect(splitSSEChunk('a\nb\nc')).toEqual({ lines: ['a', 'b'], rest: 'c' });
  });

  it('尾换行时残段为空', () => {
    expect(splitSSEChunk('a\n')).toEqual({ lines: ['a'], rest: '' });
  });
});

describe('parseEventName / parseEventData', () => {
  it('event 行', () => {
    expect(parseEventName('event: complete')).toBe('complete');
    expect(parseEventName('data: x')).toBeNull();
  });

  it('data 行保留冒号后原文', () => {
    expect(parseEventData('data: {"a":1}')).toBe('{"a":1}');
    expect(parseEventData('event: message')).toBeNull();
  });
});

describe('safeParse', () => {
  it('合法 JSON', () => {
    expect(safeParse<{ a: number }>('{"a":1}')).toEqual({ a: 1 });
  });

  it('非法 JSON 返回 null 而不是抛错', () => {
    expect(safeParse('not-json')).toBeNull();
  });
});
