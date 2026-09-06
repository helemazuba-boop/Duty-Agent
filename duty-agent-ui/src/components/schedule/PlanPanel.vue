<script setup lang="ts">
/**
 * PlanPanel — AI 排班页右栏的"当前方案"面板。
 * 自包含:内部消费 useScheduleChat 的方案数据;展示 14 天迷你排班,
 * 并用发送前签名标出"本次更新"的日子(live 色内嵌条)。
 * 同一个组件同时用于右栏和窄屏 Drawer。
 */
import { computed } from 'vue';
import { RouterLink } from 'vue-router';
import Panel from '@/components/ui/Panel.vue';
import PersonChip from '@/components/ui/PersonChip.vue';
import { useScheduleChat } from '@/composables/useScheduleChat';
import { useToday } from '@/composables/useToday';
import { personsOf, isWorkday } from '@/utils/date';
import type { ScheduleEntry } from '@/types';

const { workspace, planUpdatedAt, planBefore } = useScheduleChat();
const today = useToday();

const pool = computed<ScheduleEntry[]>(() => workspace.value?.state?.schedule_pool ?? []);
const poolByDate = computed(() => {
  const map = new Map<string, ScheduleEntry>();
  for (const entry of pool.value) map.set(entry.date, entry);
  return map;
});

const todayIso = computed(() => today.value);

/** 迷你两周视图:今天起的 14 天 */
const miniDays = computed(() => {
  const days: { iso: string; dayLabel: string; persons: string[]; isToday: boolean; gap: boolean; rest: boolean }[] = [];
  const cursor = new Date(`${todayIso.value}T00:00:00`);
  for (let i = 0; i < 14; i += 1) {
    const iso = `${cursor.getFullYear()}-${String(cursor.getMonth() + 1).padStart(2, '0')}-${String(cursor.getDate()).padStart(2, '0')}`;
    const persons = personsOf(poolByDate.value.get(iso));
    const workday = isWorkday(cursor);
    days.push({
      iso,
      dayLabel: `周${'日一二三四五六'[cursor.getDay()]} ${cursor.getDate()}`,
      persons,
      isToday: i === 0,
      gap: persons.length === 0 && workday,
      rest: persons.length === 0 && !workday,
    });
    cursor.setDate(cursor.getDate() + 1);
  }
  return days;
});

const planSummary = computed(() => {
  const total = pool.value.length;
  const upcoming = pool.value.filter((e) => e.date >= todayIso.value).length;
  const gaps = miniDays.value.filter((d) => d.gap).length;
  return { total, upcoming, gaps };
});

/** 本次 AI 执行后发生变化的日期(与发送前签名对比) */
const changedDates = computed(() => {
  const before = planBefore.value;
  if (before.size === 0) return new Set<string>();
  return new Set(
    miniDays.value
      .filter((d) => before.get(d.iso) !== d.persons.join('|'))
      .map((d) => d.iso),
  );
});

const subtitle = computed(() => {
  const base = `共 ${planSummary.value.total} 条 · 未来两周缺口 ${planSummary.value.gaps} 天`;
  return changedDates.value.size > 0 ? `${base} · 本次更新 ${changedDates.value.size} 天` : base;
});
</script>

<template>
  <Panel title="当前方案" :padded="false" :subtitle="subtitle" v-flash="planUpdatedAt">
    <div class="plan-days">
      <div
        v-for="day in miniDays"
        :key="day.iso"
        class="plan-day"
        :class="{
          'plan-day--today': day.isToday,
          'plan-day--gap': day.gap,
          'plan-day--changed': changedDates.has(day.iso),
        }"
      >
        <span class="plan-day__label da-tnum">{{ day.dayLabel }}</span>
        <span class="plan-day__chips">
          <template v-if="day.persons.length">
            <PersonChip v-for="name in day.persons" :key="name" :name="name" />
          </template>
          <span v-else-if="day.gap" class="plan-day__gap-mark">未排</span>
          <span v-else class="plan-day__rest">休</span>
        </span>
      </div>
    </div>
    <div class="plan-foot">
      <RouterLink to="/arrangement" class="plan-foot__link">打开完整排班表 →</RouterLink>
    </div>
  </Panel>
</template>

<style scoped>
.plan-days {
  display: flex;
  flex-direction: column;
}

.plan-day {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 16px;
  border-bottom: 1px solid var(--dt-border-2);
}

.plan-day:last-child {
  border-bottom: 0;
}

.plan-day--today {
  background: var(--dt-primary-soft);
}

.plan-day--gap .plan-day__label {
  color: var(--dt-warning);
}

/* 本次 AI 更新的日子:live 色内嵌条 */
.plan-day--changed {
  box-shadow: inset 2px 0 0 var(--dt-live);
}

.plan-day__label {
  font-size: 12px;
  color: var(--dt-text-2);
  flex-shrink: 0;
}

.plan-day__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  justify-content: flex-end;
}

.plan-day__gap-mark {
  font-size: 11px;
  color: var(--dt-warning);
}

.plan-day__rest {
  font-size: 11px;
  color: var(--dt-text-3);
}

.plan-foot {
  padding: 10px 16px;
  border-top: 1px solid var(--dt-border-2);
}

.plan-foot__link {
  font-size: 12px;
  color: var(--dt-primary);
  text-decoration: none;
}

.plan-foot__link:hover {
  text-decoration: underline;
}
</style>
