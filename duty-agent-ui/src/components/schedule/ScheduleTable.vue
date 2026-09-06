<script setup lang="ts">
import { computed } from 'vue';
import { Table } from 'ant-design-vue';
import { EditOutlined } from '@ant-design/icons-vue';
import type { ScheduleEntry } from '@/types';
import PersonChip from '@/components/ui/PersonChip.vue';

interface Props {
  schedulePool?: ScheduleEntry[];
}

interface Emits {
  (e: 'rowEdit', date: string): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const todayIso = (() => {
  const n = new Date();
  return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, '0')}-${String(n.getDate()).padStart(2, '0')}`;
})();

const columns = [
  { title: '日期', dataIndex: 'date', width: 150, key: 'date', sorter: (a: Row, b: Row) => a.date.localeCompare(b.date) },
  { title: '值班人', key: 'persons', minWidth: 180 },
  { title: '区域安排', key: 'areas', minWidth: 220 },
  { title: '备注', dataIndex: 'note', ellipsis: true },
  { title: '操作', width: 64, align: 'center' as const, key: 'action' },
];

interface Row extends ScheduleEntry {
  key: string;
  weekday: string;
  weekKey: string;
  persons: string[];
  areas: [string, string[]][];
}

const weekKeyOf = (iso: string): string => {
  const d = new Date(`${iso}T00:00:00`);
  // 以周一作为一周的 key
  const monday = new Date(d);
  monday.setDate(d.getDate() - ((d.getDay() + 6) % 7));
  return `${monday.getFullYear()}-${String(monday.getMonth() + 1).padStart(2, '0')}-${String(monday.getDate()).padStart(2, '0')}`;
};

const dataSource = computed<Row[]>(() =>
  (props.schedulePool || [])
    .map((entry) => {
      const areas = Object.entries(entry.area_assignments ?? {}) as [string, string[]][];
      return {
        ...entry,
        key: entry.date,
        weekday: new Date(`${entry.date}T00:00:00`).toLocaleDateString('zh-CN', { weekday: 'short' }),
        weekKey: weekKeyOf(entry.date),
        persons: [...new Set(areas.flatMap(([, list]) => list))],
        areas,
      };
    })
    .sort((a, b) => a.date.localeCompare(b.date)),
);

/** 每周第一行加周分隔线 */
const rowClassName = (record: Row, index: number) => {
  if (index > 0 && record.weekKey !== dataSource.value[index - 1]?.weekKey) {
    return 'da-row-week-start';
  }
  return '';
};
</script>

<template>
  <div class="stable">
    <Table
      :columns="columns"
      :data-source="dataSource"
      :pagination="{
        pageSize: 12,
        showSizeChanger: true,
        pageSizeOptions: ['10', '12', '20', '30'],
        showTotal: (total: number) => `共 ${total} 条`,
      }"
      size="middle"
      row-key="date"
      :row-class-name="rowClassName as any"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'date'">
          <div class="stable__date da-tnum" :class="{ 'stable__date--past': record.date < todayIso }">
            {{ record.date }}
          </div>
          <div class="stable__weekday">{{ record.weekday }}</div>
        </template>

        <template v-else-if="column.key === 'persons'">
          <div class="stable__chips">
            <PersonChip v-for="name in record.persons" :key="name" :name="name" avatar />
          </div>
        </template>

        <template v-else-if="column.key === 'areas'">
          <div v-if="record.areas.length" class="stable__areas">
            <div v-for="[area, persons] in record.areas" :key="area" class="stable__area">
              <span class="stable__area-name">{{ area }}</span>
              <span class="stable__area-persons">{{ persons.join('、') }}</span>
            </div>
          </div>
          <span v-else class="stable__none">暂无安排</span>
        </template>

        <template v-else-if="column.key === 'action'">
          <a-button size="small" type="text" @click="emit('rowEdit', record.date)">
            <template #icon><EditOutlined /></template>
          </a-button>
        </template>
      </template>
    </Table>

    <div v-if="!props.schedulePool?.length" class="empty-state">
      <div class="empty-state-title">暂无排班数据</div>
      <div class="empty-state-desc">点击右上角「新建排班」添加</div>
    </div>
  </div>
</template>

<style scoped>
.stable :deep(.da-row-week-start > td) {
  border-top: 2px solid var(--dt-border);
}

.stable__date {
  font-weight: 600;
  font-size: 13px;
  color: var(--dt-text);
}

.stable__date--past {
  color: var(--dt-text-3);
  font-weight: 500;
}

.stable__weekday {
  font-size: 11px;
  color: var(--dt-text-2);
  margin-top: 1px;
}

.stable__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.stable__areas {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.stable__area {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
}

.stable__area-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--dt-text-2);
  flex-shrink: 0;
}

.stable__area-persons {
  font-size: 13px;
  color: var(--dt-text);
}

.stable__none {
  color: var(--dt-text-3);
  font-size: 12px;
}
</style>
