# 拖拽选型结论：SortableJS（现状） vs @dnd-kit/vue（备选）

日期：2026-09-24｜范围：duty-agent-ui 名单页及未来表格拖拽｜结论：**名单页保持 SortableJS，dnd-kit 定为未来复杂拖拽默认选型**，本次不重写。

## 对比

| 维度 | SortableJS 1.15（已落地） | @dnd-kit/vue 0.5 |
|---|---|---|
| 触屏 | `delay + delayOnTouchOnly` 长按拖，与滚动共存已验证模式 | pointer-first，天生触屏友好，无需长按 hack |
| antd Table tbody 挂载 | 直接绑 `tr.ant-table-row`，无 wrapper 限制 | 需 `useSortable` 逐行 + 手动 transform，与 Table 分页/虚拟行兼容成本高 |
| 跨页/跨表拖拽 | 不支持（当页内重排，见 `mergePageOrder`） | `DndContext` 多容器原生支持 |
| 无障碍 | 弱（需自补 aria-live） | 强（announcements、键盘排序内置） |
| 体积/成熟度 | ~40KB，十年老库，API 稳定 | 新（vue 版 0.x），API 仍在变 |
| 版本现状 | `sortablejs@1.15 + @types`，构建通过 | 未引入 |

## 决策

1. 名单页（单表、当页、handle 拖拽）：SortableJS 已够用且已测，保持不动，避免 churn。
2. 未来出现跨表/跨页拖拽（如排班表跨天拖人）或无障碍审计要求时，默认选 dnd-kit，不再评估其他库。
3. 参考来源：VRCX（MIT）`package.json` 同栈验证了 pointer-first 路线，但其场景是鼠标 dense 列表，不直接解决触屏问题，故只借结论不搬代码。

## 落点

- `duty-agent-ui/src/pages/RosterPage.vue`：SortableJS，当页 `mergePageOrder` 合并全局。
- `duty-agent-ui/src/utils/rosterOrder.ts`：顺序算法纯函数，有单测，换库时复用。
