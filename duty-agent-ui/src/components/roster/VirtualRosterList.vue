<script setup lang="ts">
/**
 * VirtualRosterList — 超大花名册的虚拟滚动兜底（阈值门控，默认不启用）。
 * 常规规模走 antd Table（拖拽/排序完整）；行数超过 VIRTUALIZE_THRESHOLD
 * 才切到这里。虚拟模式只保留上下移调序，拖拽与列排序禁用（见 props.disabled）。
 * 无 antd 依赖，方便单测。
 */
import { computed, ref } from 'vue';
import { useVirtualizer } from '@tanstack/vue-virtual';

export interface VirtualRosterRow {
  id: number;
  name: string;
  active: boolean;
  dutyCount: number;
  lastDuty: string | null;
}

const props = defineProps<{
  rows: VirtualRosterRow[];
  disabled?: boolean;
  firstId?: number;
  lastId?: number;
}>();

const emit = defineEmits<{
  (e: 'move-up', id: number): void;
  (e: 'move-down', id: number): void;
}>();

const ROW_H = 52;

const scrollRef = ref<HTMLElement | null>(null);
void scrollRef;

const virtualizer = useVirtualizer(
  computed(() => ({
    count: props.rows.length,
    getScrollElement: () => scrollRef.value,
    estimateSize: () => ROW_H,
    overscan: 5,
    // 无布局环境（单测/jsdom）兜底：先按 560px 视口渲染
    initialRect: { width: 800, height: 560 },
  })),
);

const virtualRows = computed(() => virtualizer.value.getVirtualItems());
const totalSize = computed(() => virtualizer.value.getTotalSize());
</script>

<template>
  <div ref="scrollRef" class="roster-vlist" role="table" aria-label="花名册（虚拟滚动）">
    <div class="roster-vlist__inner" :style="{ height: `${totalSize}px` }">
      <div
        v-for="v in virtualRows"
        :key="props.rows[v.index]!.id"
        class="roster-vlist__row"
        :style="{ transform: `translateY(${v.start}px)`, height: `${v.size}px` }"
        role="row"
      >
        <span class="roster-vlist__name">{{ props.rows[v.index]!.name }}</span>
        <span
          class="roster-vlist__status"
          :class="props.rows[v.index]!.active ? 'roster-vlist__status--ok' : 'roster-vlist__status--off'"
        >
          {{ props.rows[v.index]!.active ? '在职' : '离职' }}
        </span>
        <span class="roster-vlist__meta da-tnum">
          本月 {{ props.rows[v.index]!.dutyCount }} 次 · {{ props.rows[v.index]!.lastDuty ?? '从未值班' }}
        </span>
        <span class="roster-vlist__ops">
          <button
            type="button"
            :disabled="props.disabled || props.rows[v.index]!.id === props.firstId"
            @click="emit('move-up', props.rows[v.index]!.id)"
            aria-label="上移"
          >↑</button>
          <button
            type="button"
            :disabled="props.disabled || props.rows[v.index]!.id === props.lastId"
            @click="emit('move-down', props.rows[v.index]!.id)"
            aria-label="下移"
          >↓</button>
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.roster-vlist {
  max-height: 560px;
  overflow-y: auto;
}

.roster-vlist__inner {
  position: relative;
}

.roster-vlist__row {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 0 16px;
  border-bottom: 1px solid var(--dt-border);
  background: var(--dt-surface);
  box-sizing: border-box;
}

.roster-vlist__name {
  font-size: 14px;
  font-weight: 600;
  color: var(--dt-text);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-vlist__status {
  font-size: 12px;
  padding: 1px 8px;
  border-radius: 999px;
  flex-shrink: 0;
}

.roster-vlist__status--ok {
  color: var(--dt-success);
  background: color-mix(in srgb, var(--dt-success) 12%, transparent);
}

.roster-vlist__status--off {
  color: var(--dt-text-3);
  background: var(--dt-surface-2);
}

.roster-vlist__meta {
  margin-left: auto;
  font-size: 12px;
  color: var(--dt-text-2);
  white-space: nowrap;
}

.roster-vlist__ops {
  display: flex;
  gap: 4px;
  flex-shrink: 0;
}

.roster-vlist__ops button {
  width: 44px;
  height: 44px;
  border: 1px solid var(--dt-border);
  border-radius: 8px;
  background: transparent;
  color: var(--dt-text-2);
  font-size: 16px;
  cursor: pointer;
}

.roster-vlist__ops button:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}
</style>
