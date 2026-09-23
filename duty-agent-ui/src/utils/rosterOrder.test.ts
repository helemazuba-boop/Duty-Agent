import { describe, it, expect } from 'vitest';
import { moveId, mergePageOrder, buildOrderMap } from './rosterOrder';

describe('moveId', () => {
  it('上移中间元素', () => {
    expect(moveId([1, 2, 3], 2, -1)).toEqual([2, 1, 3]);
  });

  it('下移中间元素', () => {
    expect(moveId([1, 2, 3], 2, 1)).toEqual([1, 3, 2]);
  });

  it('首位上移/末位下移保持不变', () => {
    expect(moveId([1, 2, 3], 1, -1)).toEqual([1, 2, 3]);
    expect(moveId([1, 2, 3], 3, 1)).toEqual([1, 2, 3]);
  });

  it('不存在的 id 保持不变', () => {
    expect(moveId([1, 2, 3], 9, 1)).toEqual([1, 2, 3]);
  });

  it('不修改入参', () => {
    const src = [1, 2, 3];
    moveId(src, 2, -1);
    expect(src).toEqual([1, 2, 3]);
  });
});

describe('mergePageOrder', () => {
  it('当页重排拼回原锚点', () => {
    expect(mergePageOrder([1, 2, 3, 4, 5, 6], [3, 4], [4, 3])).toEqual([1, 2, 4, 3, 5, 6]);
  });

  it('末页重排', () => {
    expect(mergePageOrder([1, 2, 3, 4], [3, 4], [4, 3])).toEqual([1, 2, 4, 3]);
  });

  it('页段不在全局时追加到末尾', () => {
    expect(mergePageOrder([1, 2], [7, 8], [8, 7])).toEqual([1, 2, 8, 7]);
  });
});

describe('buildOrderMap', () => {
  it('idx*10 步进', () => {
    expect(buildOrderMap([5, 3])).toEqual(new Map([[5, 0], [3, 10]]));
  });

  it('空数组', () => {
    expect(buildOrderMap([])).toEqual(new Map());
  });
});
