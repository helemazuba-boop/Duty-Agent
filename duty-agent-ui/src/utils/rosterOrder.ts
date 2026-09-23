/**
 * 手动顺序纯函数（花名册 orderMap 算法层）。
 * 页面只负责读写 orderMap 与 persist，顺序算法全部可单元测试。
 * 约定：orderMap 值为 idx * 10，前端私有显示顺序，后端 schema 不收 order 字段。
 */

/** 上/下移一位；越界或不存在返回等值拷贝 */
export function moveId(ids: number[], id: number, dir: -1 | 1): number[] {
  const next = ids.slice();
  const i = next.indexOf(id);
  const j = i + dir;
  if (i < 0 || j < 0 || j >= next.length) return next;
  [next[i], next[j]] = [next[j], next[i]];
  return next;
}

/**
 * 当页重排合并回全局顺序。
 * @param fullIds 全局 id 序列（旧）
 * @param pageIdsOld 当页 id 序列（旧）
 * @param pageIdsNew 当页 id 序列（新）
 * @returns 新全局序列：页段整体搬回原锚点
 */
export function mergePageOrder(fullIds: number[], pageIdsOld: number[], pageIdsNew: number[]): number[] {
  const pageSet = new Set(pageIdsOld);
  const rest = fullIds.filter((id) => !pageSet.has(id));
  const anchor = fullIds.findIndex((id) => pageSet.has(id));
  rest.splice(anchor < 0 ? rest.length : anchor, 0, ...pageIdsNew);
  return rest;
}

/** id 序列 → orderMap */
export function buildOrderMap(ids: number[]): Map<number, number> {
  const map = new Map<number, number>();
  ids.forEach((id, idx) => map.set(id, idx * 10));
  return map;
}
