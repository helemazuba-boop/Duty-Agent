<script setup lang="ts">
import { computed } from 'vue';
import { Badge, Typography } from 'ant-design-vue';
import type { ScheduleEntry } from '@/types';

interface Props {
  schedulePool?: ScheduleEntry[];
}

interface Emits {
  (e: 'dateSelect', date: string): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const { Text } = Typography;

const today = new Date();
const currentYear = today.getFullYear();
const currentMonth = today.getMonth();

const daysInMonth = computed(() => new Date(currentYear, currentMonth + 1, 0).getDate());
const firstDayOfWeek = computed(() => new Date(currentYear, currentMonth, 1).getDay());
const monthName = computed(() => `${currentYear}年${currentMonth + 1}月`);

const scheduleMap = computed(() => {
  const map: Record<string, ScheduleEntry> = {};
  for (const entry of props.schedulePool || []) {
    map[entry.date] = entry;
  }
  return map;
});

const makeDateStr = (day: number) =>
  `${currentYear}-${String(currentMonth + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;

const isToday = (day: number) => makeDateStr(day) === today.toISOString().split('T')[0];
const isPast = (day: number) => new Date(makeDateStr(day)) < new Date(today.toISOString().split('T')[0] + 'T00:00:00');

const handleDayClick = (day: number) => {
  emit('dateSelect', makeDateStr(day));
};

const weekDays = ['日', '一', '二', '三', '四', '五', '六'];
</script>

<template>
  <div class="calendar-wrapper">
    <div class="calendar-card">
      <!-- Month header -->
      <div class="calendar-header">
        <Text class="calendar-month-name">{{ monthName }}</Text>
      </div>

      <!-- Weekday row -->
      <div class="calendar-weekdays">
        <div
          v-for="d in weekDays"
          :key="d"
          class="calendar-weekday"
          :class="{ 'calendar-weekday--weekend': d === '日' || d === '六' }"
        >
          {{ d }}
        </div>
      </div>

      <!-- Calendar grid -->
      <div class="calendar-grid">
        <!-- Empty leading cells -->
        <div
          v-for="i in firstDayOfWeek"
          :key="`empty-${i}`"
          class="calendar-cell calendar-cell--empty"
        />

        <!-- Day cells -->
        <div
          v-for="day in daysInMonth"
          :key="day"
          class="calendar-cell"
          :class="{
            'calendar-cell--today': isToday(day),
            'calendar-cell--past': isPast(day),
          }"
          @click="handleDayClick(day)"
        >
          <div class="calendar-day-number">
            <span v-if="isToday(day)" class="today-dot" />
            {{ day }}
          </div>
          <div v-if="scheduleMap[makeDateStr(day)]" class="calendar-day-entries">
            <Badge
              status="processing"
              :text="Object.values(scheduleMap[makeDateStr(day)].area_assignments || {})
                .flat()
                .slice(0, 2)
                .join(', ')"
              class="entry-badge"
            />
          </div>
          <div v-else class="calendar-day-empty">-</div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.calendar-wrapper {
  width: 100%;
}

.calendar-card {
  background: #fff;
  border-radius: var(--da-radius-md);
  box-shadow: var(--da-shadow-sm);
  border: 1px solid var(--da-border);
  padding: 20px;
}

.calendar-header {
  text-align: center;
  margin-bottom: 16px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--da-border);
}

.calendar-month-name {
  font-size: 18px;
  font-weight: 600;
  color: var(--da-text-primary);
  font-family: inherit;
}

.calendar-weekdays {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  margin-bottom: 4px;
}

.calendar-weekday {
  text-align: center;
  font-size: 12px;
  font-weight: 600;
  color: var(--da-text-secondary);
  padding: 6px 0;
}

.calendar-weekday--weekend {
  color: #ff4d4f;
}

.calendar-grid {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  gap: 4px;
}

.calendar-cell {
  min-height: 72px;
  padding: 6px 4px;
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.2s;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.calendar-cell--empty {
  cursor: default;
  background: transparent;
}

.calendar-cell:not(.calendar-cell--empty):hover {
  background: #e6f7ff;
  transform: translateY(-1px);
}

.calendar-cell--today {
  background: #e6f7ff;
  border: 2px solid #1890ff;
}

.calendar-cell--today:hover {
  background: #bae7ff;
}

.calendar-cell--past .calendar-day-number {
  color: var(--da-text-muted);
}

.calendar-day-number {
  font-size: 14px;
  font-weight: 600;
  color: var(--da-text-primary);
  display: flex;
  align-items: center;
  gap: 4px;
  justify-content: center;
}

.today-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #1890ff;
  display: inline-block;
}

.calendar-day-entries {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  overflow: hidden;
}

:deep(.entry-badge) {
  font-size: 10px;
  line-height: 1.3;
}

:deep(.entry-badge .ant-badge-status-text) {
  color: #1890ff;
  font-size: 10px;
}

.calendar-day-empty {
  text-align: center;
  font-size: 11px;
  color: var(--da-text-muted);
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
}
</style>
