<script setup lang="ts">
import { computed, ref } from 'vue';
import { Tooltip } from 'ant-design-vue';
import { LeftOutlined, RightOutlined } from '@ant-design/icons-vue';
import type { ScheduleEntry } from '@/types';
import PersonChip from '@/components/ui/PersonChip.vue';
import { useToday } from '@/composables/useToday';
import { personsOf, isWorkday } from '@/utils/date';

interface Props {
  schedulePool?: ScheduleEntry[];
}

interface Emits {
  (e: 'dateSelect', date: string): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

// ======== 月份导航(不固定当前月) ========
const viewYear = ref(new Date().getFullYear());
const viewMonth = ref(new Date().getMonth()); // 0-based

const monthLabel = computed(() => `${viewYear.value} 年 ${viewMonth.value + 1} 月`);
const monthPrefix = computed(() => `${viewYear.value}-${String(viewMonth.value + 1).padStart(2, '0')}`);

const shiftMonth = (delta: number) => {
  const d = new Date(viewYear.value, viewMonth.value + delta, 1);
  viewYear.value = d.getFullYear();
  viewMonth.value = d.getMonth();
};

const backToToday = () => {
  const now = new Date();
  viewYear.value = now.getFullYear();
  viewMonth.value = now.getMonth();
};

const fmtIso = (y: number, m: number, day: number) =>
  `${y}-${String(m + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;

const today = useToday();

// ======== 排班索引 ========
const scheduleMap = computed(() => {
  const map = new Map<string, ScheduleEntry>();
  for (const entry of props.schedulePool || []) map.set(entry.date, entry);
  return map;
});

/** 冲突:同一天同一人被排进多个区域。Tooltip 写出是谁,避免只有红点没有信息 */
const conflictNames = (entry: ScheduleEntry | undefined): string[] => {
  if (!entry) return [];
  const all = Object.values(entry.area_assignments ?? {}).flat();
  const seen = new Set<string>();
  const dup = new Set<string>();
  for (const name of all) {
    if (seen.has(name)) dup.add(name);
    seen.add(name);
  }
  return [...dup];
};

/** 未来 7 天内未排的工作日 → 虚线警告边(周末不告警) */
const isWarningGap = (iso: string): boolean => {
  if (iso <= today.value) return false;
  if (scheduleMap.value.has(iso) && personsOf(scheduleMap.value.get(iso)).length > 0) return false;
  const cursor = new Date(`${iso}T00:00:00`);
  if (!isWorkday(cursor)) return false;
  const diff = (cursor.getTime() - new Date(`${today.value}T00:00:00`).getTime()) / 86400000;
  return diff <= 7;
};

// ======== 网格 ========
interface CalendarCell {
  iso: string;
  day: number;
  isToday: boolean;
  isPast: boolean;
  isWeekend: boolean;
  warning: boolean;
  conflicts: string[];
  persons: string[];
}

const cells = computed<CalendarCell[]>(() => {
  const first = new Date(viewYear.value, viewMonth.value, 1);
  const startOffset = first.getDay(); // 周日开头,与表头一致
  const result: CalendarCell[] = [];
  const cursor = new Date(first);
  cursor.setDate(cursor.getDate() - startOffset);

  for (let i = 0; i < 42; i += 1) {
    const iso = fmtIso(cursor.getFullYear(), cursor.getMonth(), cursor.getDate());
    const entry = scheduleMap.value.get(iso);
    result.push({
      iso,
      day: cursor.getDate(),
      isToday: iso === today.value,
      isPast: iso < today.value,
      isWeekend: !isWorkday(cursor),
      warning: isWarningGap(iso),
      conflicts: conflictNames(entry),
      persons: personsOf(entry),
    });
    cursor.setDate(cursor.getDate() + 1);
  }
  return result;
});

const weekDays = ['日', '一', '二', '三', '四', '五', '六'];
const CHIP_LIMIT = 3;
</script>

<template>
  <div class="cal">
    <!-- 月份导航 -->
    <div class="cal__nav">
      <div class="cal__month">
        <span class="cal__month-label">{{ monthLabel }}</span>
        <button type="button" class="cal__nav-btn" @click="backToToday">今天</button>
      </div>
      <div class="cal__nav-group">
        <button type="button" class="cal__nav-btn cal__nav-btn--icon" aria-label="上个月" @click="shiftMonth(-1)">
          <LeftOutlined />
        </button>
        <button type="button" class="cal__nav-btn cal__nav-btn--icon" aria-label="下个月" @click="shiftMonth(1)">
          <RightOutlined />
        </button>
      </div>
    </div>

    <!-- 表头 -->
    <div class="cal__weekdays">
      <span
        v-for="(d, i) in weekDays"
        :key="d"
        class="cal__weekday"
        :class="{ 'cal__weekday--weekend': i === 0 || i === 6 }"
      >{{ d }}</span>
    </div>

    <!-- 网格 -->
    <div class="cal__grid">
      <Tooltip
        v-for="cell in cells"
        :key="cell.iso"
        placement="top"
        :title="cell.conflicts.length ? `${cell.conflicts.join('、')} 被排入多个区域` : ''"
      >
        <button
          type="button"
          class="cal__cell"
          :class="{
            'cal__cell--out': !cell.iso.startsWith(monthPrefix),
            'cal__cell--today': cell.isToday,
            'cal__cell--weekend': cell.isWeekend && !cell.isToday,
            'cal__cell--past': cell.isPast,
            'cal__cell--warning': cell.warning,
          }"
          @click="emit('dateSelect', cell.iso)"
        >
          <span class="cal__dayrow">
            <span class="cal__daynum da-tnum">{{ cell.day }}</span>
            <span v-if="cell.conflicts.length" class="cal__conflict" />
          </span>
          <span class="cal__chips">
            <PersonChip v-for="name in cell.persons.slice(0, CHIP_LIMIT)" :key="name" :name="name" />
            <PersonChip v-if="cell.persons.length > CHIP_LIMIT" :name="`+${cell.persons.length - CHIP_LIMIT}`" neutral />
          </span>
        </button>
      </Tooltip>
    </div>
  </div>
</template>

<style scoped>
.cal {
  background: var(--dt-surface);
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius-lg);
  padding: 16px;
}

/* ---- 导航 ---- */
.cal__nav {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.cal__month {
  display: flex;
  align-items: center;
  gap: 10px;
}

.cal__month-label {
  font-size: 16px;
  font-weight: 600;
  color: var(--dt-text);
}

.cal__nav-group {
  display: flex;
  gap: 4px;
}

.cal__nav-btn {
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius);
  background: transparent;
  color: var(--dt-text-2);
  font-size: 12px;
  cursor: pointer;
  transition: color 0.15s ease, border-color 0.15s ease;
}

.cal__nav-btn:hover {
  color: var(--dt-text);
  border-color: var(--dt-border-strong);
}

.cal__nav-btn--icon {
  width: 26px;
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

/* ---- 表头 ---- */
.cal__weekdays {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  margin-bottom: 4px;
}

.cal__weekday {
  text-align: center;
  font-size: 12px;
  color: var(--dt-text-2);
  padding: 4px 0;
}

.cal__weekday--weekend {
  color: var(--dt-text-3);
}

/* ---- 网格 ---- */
.cal__grid {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  gap: 4px;
}

/* Tooltip 包裹后让按钮撑满格子 */
.cal__grid > :deep(.ant-tooltip-wrapper) {
  display: block;
}

.cal__cell {
  min-height: 96px;
  width: 100%;
  padding: 6px;
  border: 1px solid var(--dt-border-2);
  border-radius: var(--dt-radius);
  background: var(--dt-surface);
  cursor: pointer;
  display: flex;
  flex-direction: column;
  gap: 4px;
  text-align: left;
  transition: border-color 0.15s ease, background 0.15s ease;
}

.cal__cell:hover {
  background: var(--dt-hover);
}

.cal__cell--out {
  opacity: 0.42;
}

.cal__cell--weekend {
  background: var(--dt-surface-2);
}

.cal__cell--past .cal__daynum {
  color: var(--dt-text-3);
}

.cal__cell--today {
  border-color: var(--dt-primary);
  background: var(--dt-primary-soft);
}

/* 今日格 hover 不被灰底盖掉(color-mix 是瞬态,允许用) */
.cal__cell--today:hover {
  background: color-mix(in srgb, var(--dt-primary) 18%, var(--dt-surface));
}

.cal__cell--warning {
  border-style: dashed;
  border-color: color-mix(in srgb, var(--dt-warning) 55%, transparent);
}

.cal__dayrow {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.cal__daynum {
  font-size: 12px;
  font-weight: 600;
  color: var(--dt-text);
}

.cal__cell--today .cal__daynum {
  color: var(--dt-primary);
}

.cal__conflict {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--dt-danger);
}

.cal__chips {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  overflow: hidden;
}
</style>
