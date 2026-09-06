<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { Empty, message } from 'ant-design-vue';
import { ReloadOutlined, CopyOutlined } from '@ant-design/icons-vue';
import { api } from '@/api/http';
import type { Workspace, ScheduleEntry, RosterPerson } from '@/types';
import PageHeader from '@/components/ui/PageHeader.vue';
import Panel from '@/components/ui/Panel.vue';
import StatCard from '@/components/ui/StatCard.vue';
import StatusDot from '@/components/ui/StatusDot.vue';
import PersonChip from '@/components/ui/PersonChip.vue';
import ArrangementEditor from '@/components/schedule/ArrangementEditor.vue';
import FirstRunWizard from '@/components/onboarding/FirstRunWizard.vue';
import { useTheme } from '@/theme/useTheme';
import { useToday } from '@/composables/useToday';
import { personChipStyle } from '@/utils/personColor';
import {
  fmtDate,
  startOfWeek,
  isWorkday,
  personsOf,
  hasDuty,
  weekdayShort,
  monthDayLabel,
  cnDateLabel,
} from '@/utils/date';

const router = useRouter();
const { resolved } = useTheme();
const today = useToday();

/** Hero 头像用人员色板的 solid/onSolid 变量 */
const personVars = (name: string) => personChipStyle(name, resolved.value);

const workspace = ref<Workspace | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);

const WIZARD_DISMISS_KEY = 'duty_first_run_dismissed';
const wizardOpen = ref(false);

const maybeOpenWizard = async () => {
  if (localStorage.getItem(WIZARD_DISMISS_KEY) === '1') return;
  try {
    const readiness = await api.getReadiness(false);
    if (!readiness.ready) wizardOpen.value = true;
  } catch {
    // backend not reachable yet; skip silently
  }
};

const onWizardCompleted = () => {
  localStorage.setItem(WIZARD_DISMISS_KEY, '1');
  refresh();
};

const refresh = async () => {
  loading.value = true;
  error.value = null;
  try {
    workspace.value = await api.getSnapshot();
  } catch (e) {
    error.value = String(e);
  } finally {
    loading.value = false;
  }
};

onMounted(() => {
  refresh();
  maybeOpenWizard();
});

// ======== 数据视图 ========
const pool = computed<ScheduleEntry[]>(() => workspace.value?.state?.schedule_pool ?? []);
const poolByDate = computed(() => {
  const map = new Map<string, ScheduleEntry>();
  for (const entry of pool.value) map.set(entry.date, entry);
  return map;
});
const roster = computed<RosterPerson[]>(() => (workspace.value?.roster ?? []).filter((r) => r.active));

const contactOf = (person: RosterPerson) =>
  String(person.contact ?? person.phone ?? person.contact_info ?? '').trim();

const contactByName = computed(() => {
  const map = new Map<string, string>();
  for (const person of roster.value) {
    const contact = contactOf(person);
    if (contact) map.set(person.name, contact);
  }
  return map;
});

// ---- Hero:今日值班 ----
const todayEntry = computed(() => poolByDate.value.get(today.value));
const todayPersons = computed(() => personsOf(todayEntry.value).slice(0, 3));
const todayOverflow = computed(() => Math.max(0, personsOf(todayEntry.value).length - 3));
const todayIsWorkday = computed(() => isWorkday(new Date(`${today.value}T00:00:00`)));

/** Hero 的内容签名:数据变了才 flash,而不是"刷新了"就闪 */
const todaySig = computed(() => todayPersons.value.join('|'));

// ---- 接下来 3 天:所有工作日缺口都标出来,周末显示"休" ----
const next3Days = computed(() => {
  const days: { iso: string; persons: string[]; missed: boolean; rest: boolean }[] = [];
  const cursor = new Date(`${today.value}T00:00:00`);
  for (let i = 1; i <= 3; i += 1) {
    cursor.setDate(cursor.getDate() + 1);
    const iso = fmtDate(cursor);
    const persons = personsOf(poolByDate.value.get(iso));
    days.push({
      iso,
      persons,
      missed: persons.length === 0 && isWorkday(cursor),
      rest: persons.length === 0 && !isWorkday(cursor),
    });
  }
  return days;
});

// ---- 未来 7 天(工作日口径):覆盖 / 缺口 ----
const next7Workdays = computed(() => {
  const list: string[] = [];
  const cursor = new Date(`${today.value}T00:00:00`);
  for (let i = 0; i < 7; i += 1) {
    if (isWorkday(cursor)) list.push(fmtDate(cursor));
    cursor.setDate(cursor.getDate() + 1);
  }
  return list;
});

const gapCount = computed(() => next7Workdays.value.filter((iso) => !hasDuty(poolByDate.value.get(iso))).length);
const coveredCount = computed(() => next7Workdays.value.length - gapCount.value);

// ---- 本月负载差(公平性:最多 vs 最少) ----
const monthLoad = computed(() => {
  const monthPrefix = today.value.slice(0, 7);
  const counts = new Map<string, number>();
  for (const person of roster.value) counts.set(person.name, 0);
  for (const entry of pool.value) {
    if (!entry.date.startsWith(monthPrefix)) continue;
    for (const name of personsOf(entry)) {
      counts.set(name, (counts.get(name) ?? 0) + 1);
    }
  }
  let maxName = '';
  let max = -1;
  let minName = '';
  let min = Number.MAX_SAFE_INTEGER;
  for (const [name, count] of counts) {
    if (count > max) {
      max = count;
      maxName = name;
    }
    if (count < min) {
      min = count;
      minName = name;
    }
  }
  if (max < 0) return { diff: 0, max: 0, min: 0, maxName: '', minName: '' };
  return { diff: max - min, max, min, maxName, minName };
});

const rosterTotal = computed(() => (workspace.value?.roster ?? []).length);
const rosterInactive = computed(() => rosterTotal.value - roster.value.length);

// ---- 本周条带(周一起,与 KPI 同口径) ----
const weekStrip = computed(() => {
  const monday = startOfWeek(new Date(`${today.value}T00:00:00`), 1);
  const days: { iso: string; label: string; monthDay: string; persons: string[]; isToday: boolean; isWeekend: boolean; isPast: boolean }[] = [];
  const cursor = new Date(monday);
  for (let i = 0; i < 7; i += 1) {
    const iso = fmtDate(cursor);
    days.push({
      iso,
      label: `周${'一二三四五六日'[cursor.getDay()]}`,
      monthDay: monthDayLabel(iso),
      persons: personsOf(poolByDate.value.get(iso)),
      isToday: iso === today.value,
      isWeekend: !isWorkday(cursor),
      isPast: iso < today.value,
    });
    cursor.setDate(cursor.getDate() + 1);
  }
  return days;
});

/** 条带签名:每天的人名串起来,变了才 flash */
const weekSig = computed(() => weekStrip.value.map((d) => d.persons.join(',')).join('|'));

// ---- 花名册概览 ----
const rosterPreview = computed(() => roster.value.slice(0, 12));
const rosterOverflow = computed(() => Math.max(0, roster.value.length - 12));

// ---- 编辑 Drawer(复用排班页编辑器) ----
const editorOpen = ref(false);
const selectedDate = ref<string | null>(null);
const openEditor = (date: string | null) => {
  selectedDate.value = date;
  editorOpen.value = true;
};
const handleEditorSave = async (entry: ScheduleEntry) => {
  try {
    await api.saveScheduleEntry(entry);
    await refresh();
    editorOpen.value = false;
  } catch (e) {
    console.error('[Dashboard] save failed:', e);
  }
};

const copyContact = async (name: string) => {
  const contact = contactByName.value.get(name);
  if (!contact) return;
  try {
    await navigator.clipboard.writeText(contact);
    message.success(`已复制 ${name} 的联系方式`);
  } catch {
    message.warning('复制失败,请手动复制');
  }
};
</script>

<template>
  <div class="page-container animate-fade-in">
    <PageHeader title="仪表盘">
      <template #subtitle>
        <StatusDot
          :status="error ? 'error' : 'ok'"
          :label="error
            ? '数据加载失败'
            : `最后更新 ${new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}`"
        />
      </template>
      <template #actions>
        <a-button :loading="loading" @click="refresh">
          <template #icon><ReloadOutlined /></template>
          刷新
        </a-button>
        <a-button type="primary" @click="router.push('/schedule')">去 AI 排班</a-button>
      </template>
    </PageHeader>

    <div v-if="error" class="da-error-tip">数据加载失败:{{ error }}</div>

    <!-- ======== Hero 行:今日值班 + 接下来 3 天 ======== -->
    <div class="hero-grid">
      <Panel class="hero-grid__main" padded v-flash="todaySig">
        <template #title>今日值班</template>
        <template #actions>
          <span class="hero-date">{{ cnDateLabel(today) }}</span>
        </template>

        <div v-if="todayPersons.length" class="hero-duty">
          <div
            v-for="name in todayPersons"
            :key="name"
            class="hero-duty__person"
            :style="personVars(name)"
          >
            <span class="hero-duty__avatar">{{ name.trim()[0] }}</span>
            <div class="hero-duty__meta">
              <div class="hero-duty__name">{{ name }}</div>
              <button
                v-if="contactByName.get(name)"
                type="button"
                class="hero-duty__contact"
                @click="copyContact(name)"
              >
                {{ contactByName.get(name) }}
                <CopyOutlined />
              </button>
            </div>
          </div>
          <PersonChip v-if="todayOverflow" :name="`+${todayOverflow} 人`" neutral />
        </div>

        <!-- 周末无人值班不是事故,不做警告样式 -->
        <div v-else-if="!todayIsWorkday" class="hero-duty__empty hero-duty__empty--rest">
          <span class="hero-duty__empty-text">今天休息</span>
          <a-button @click="openEditor(today)">安排值班</a-button>
        </div>

        <div v-else class="hero-duty__empty">
          <span class="hero-duty__empty-text">今天无人值班</span>
          <a-button danger @click="openEditor(today)">补排今天</a-button>
        </div>
      </Panel>

      <Panel class="hero-grid__side" title="接下来 3 天" :padded="false">
        <div class="next-days">
          <button
            v-for="day in next3Days"
            :key="day.iso"
            type="button"
            class="next-days__row"
            :class="{ 'next-days__row--missed': day.missed }"
            @click="openEditor(day.iso)"
          >
            <span class="next-days__date da-tnum">
              {{ monthDayLabel(day.iso) }} {{ weekdayShort(day.iso) }}
            </span>
            <span v-if="day.persons.length" class="next-days__chips">
              <PersonChip v-for="name in day.persons" :key="name" :name="name" />
            </span>
            <span v-else-if="day.missed" class="next-days__mark next-days__mark--missed">未排</span>
            <span v-else class="next-days__mark">休</span>
          </button>
        </div>
      </Panel>
    </div>

    <!-- ======== KPI 行(flash 绑在各卡自己的数据签名上) ======== -->
    <div class="kpi-grid stagger-children">
      <StatCard
        label="未来 7 天覆盖"
        :value="`${coveredCount}/${next7Workdays.length}`"
        :hint="gapCount === 0 ? '工作日全覆盖' : `${gapCount} 个工作日未排`"
        :tone="gapCount === 0 ? 'success' : 'warning'"
        :flash-key="`${coveredCount}/${next7Workdays.length}`"
      />
      <StatCard
        label="本周缺口"
        :value="gapCount"
        hint="点击查看排班表"
        :tone="gapCount === 0 ? 'success' : 'warning'"
        to="/arrangement"
        :flash-key="gapCount"
      />
      <StatCard
        label="本月负载差"
        :value="monthLoad.diff ? `${monthLoad.diff} 次` : '0'"
        :hint="monthLoad.maxName
          ? `最多 ${monthLoad.maxName} ${monthLoad.max} 次 · 最少 ${monthLoad.minName} ${monthLoad.min} 次`
          : '本月暂无排班'"
        :tone="monthLoad.diff >= 3 ? 'warning' : 'default'"
        :flash-key="monthLoad.diff"
      />
      <StatCard
        label="在职人数"
        :value="roster.length"
        :hint="rosterInactive ? `${rosterInactive} 人离职` : '全员在职'"
        :flash-key="roster.length"
      />
    </div>

    <!-- ======== 中部:本周条带 + 花名册概览 ======== -->
    <div class="mid-grid">
      <Panel class="mid-grid__main" title="本周安排" padded v-flash="weekSig">
        <template #actions>
          <RouterLink to="/arrangement" class="da-link">查看全部</RouterLink>
        </template>

        <div class="week-strip">
          <button
            v-for="day in weekStrip"
            :key="day.iso"
            type="button"
            class="week-strip__col"
            :class="{
              'week-strip__col--today': day.isToday,
              'week-strip__col--weekend': day.isWeekend && !day.isToday,
              'week-strip__col--past': day.isPast,
            }"
            @click="openEditor(day.iso)"
          >
            <span class="week-strip__day">{{ day.label }}</span>
            <span class="week-strip__date da-tnum">{{ day.monthDay }}</span>
            <span class="week-strip__chips">
              <PersonChip v-for="name in day.persons" :key="name" :name="name" />
            </span>
          </button>
        </div>
      </Panel>

      <Panel class="mid-grid__side" title="花名册" :padded="false">
        <template #actions>
          <RouterLink to="/roster" class="da-link">管理</RouterLink>
        </template>
        <div v-if="roster.length" class="roster-overview">
          <div class="roster-overview__chips">
            <PersonChip v-for="person in rosterPreview" :key="person.name" :name="person.name" avatar />
            <PersonChip v-if="rosterOverflow" :name="`+${rosterOverflow}`" neutral />
          </div>
          <div class="roster-overview__meta">共 {{ roster.length }} 人在职</div>
        </div>
        <Empty v-else description="还没有成员" :image="Empty.PRESENTED_IMAGE_SIMPLE">
          <a-button type="primary" @click="router.push('/roster')">添加第一位成员</a-button>
        </Empty>
      </Panel>
    </div>

    <ArrangementEditor
      v-model:open="editorOpen"
      :date="selectedDate"
      :schedule-pool="pool"
      :roster="workspace?.roster"
      @save="handleEditorSave"
    />
    <FirstRunWizard v-model:open="wizardOpen" @completed="onWizardCompleted" />
  </div>
</template>

<style scoped>
.da-link {
  font-size: 13px;
  color: var(--dt-primary);
  text-decoration: none;
}

.da-link:hover {
  text-decoration: underline;
}

.da-error-tip {
  padding: 8px 12px;
  border: 1px solid color-mix(in srgb, var(--dt-danger) 35%, transparent);
  background: color-mix(in srgb, var(--dt-danger) 8%, transparent);
  color: var(--dt-danger);
  border-radius: var(--dt-radius);
  font-size: 13px;
}

.hero-date {
  font-size: 12px;
  color: var(--dt-text-2);
}

/* ---- Hero ---- */
.hero-grid {
  display: grid;
  grid-template-columns: 2fr 1fr;
  gap: 16px;
}

.hero-duty {
  display: flex;
  align-items: center;
  gap: 20px;
  flex-wrap: wrap;
  min-height: 64px;
}

.hero-duty__person {
  display: flex;
  align-items: center;
  gap: 12px;
}

.hero-duty__avatar {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border-radius: 999px;
  background: var(--person-solid);
  color: var(--person-on-solid);
  font-size: 22px;
  font-weight: 600;
}

.hero-duty__meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.hero-duty__name {
  font-size: 24px;
  line-height: 32px;
  font-weight: 600;
  color: var(--dt-text);
}

.hero-duty__contact {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  border: 0;
  background: none;
  font-size: 12px;
  color: var(--dt-text-2);
  cursor: pointer;
}

.hero-duty__contact:hover {
  color: var(--dt-primary);
}

.hero-duty__empty {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 64px;
}

.hero-duty__empty-text {
  font-size: 14px;
  color: var(--dt-warning);
}

.hero-duty__empty--rest .hero-duty__empty-text {
  color: var(--dt-text-2);
}

/* ---- 接下来 3 天 ---- */
.next-days {
  display: flex;
  flex-direction: column;
  padding: 4px 8px;
}

.next-days__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 10px 12px;
  border: 0;
  border-bottom: 1px solid var(--dt-border-2);
  background: transparent;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s ease;
}

.next-days__row:last-child {
  border-bottom: 0;
}

.next-days__row:hover {
  background: var(--dt-hover);
}

.next-days__row--missed .next-days__date {
  color: var(--dt-warning);
}

.next-days__date {
  font-size: 13px;
  color: var(--dt-text-2);
  flex-shrink: 0;
}

.next-days__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  justify-content: flex-end;
}

.next-days__mark {
  font-size: 12px;
  color: var(--dt-text-3);
}

.next-days__mark--missed {
  color: var(--dt-warning);
}

/* ---- KPI ---- */
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 16px;
}

/* ---- 中部 ---- */
.mid-grid {
  display: grid;
  grid-template-columns: 2fr 1fr;
  gap: 16px;
}

.week-strip {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  gap: 6px;
}

.week-strip__col {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  padding: 10px 4px 12px;
  border: 1px solid var(--dt-border-2);
  border-radius: var(--dt-radius);
  background: var(--dt-surface);
  cursor: pointer;
  transition: border-color 0.15s ease, background 0.15s ease;
  overflow: hidden;
}

.week-strip__col:hover {
  border-color: var(--dt-border-strong);
  background: var(--dt-hover);
}

.week-strip__col--weekend {
  background: var(--dt-surface-2);
}

/* 过去的列只降文字,不压 chip 对比度 */
.week-strip__col--past .week-strip__day,
.week-strip__col--past .week-strip__date {
  color: var(--dt-text-3);
}

.week-strip__col--past .week-strip__chips {
  filter: saturate(0.55);
}

.week-strip__col--today {
  border-color: var(--dt-primary);
  background: var(--dt-primary-soft);
}

.week-strip__col--today::before {
  content: '';
  position: absolute;
  inset: 0 0 auto;
  height: 2px;
  background: var(--dt-primary);
}

.week-strip__day {
  font-size: 12px;
  color: var(--dt-text-2);
}

.week-strip__col--today .week-strip__day {
  color: var(--dt-primary);
  font-weight: 600;
}

.week-strip__date {
  font-size: 13px;
  font-weight: 600;
  color: var(--dt-text);
}

.week-strip__chips {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  margin-top: 6px;
  min-height: 20px;
}

/* ---- 花名册概览 ---- */
.roster-overview {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.roster-overview__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.roster-overview__meta {
  font-size: 12px;
  color: var(--dt-text-2);
}

/* ---- 响应式(桌面内部工具,只考虑两档) ---- */
@media (max-width: 1280px) {
  .hero-grid,
  .mid-grid {
    grid-template-columns: 1fr;
  }

  .kpi-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}
</style>
