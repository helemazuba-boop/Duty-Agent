<script setup lang="ts">
import { computed } from 'vue';
import { Tag } from 'ant-design-vue';
import { EditOutlined } from '@ant-design/icons';
import type { ScheduleEntry } from '@/types';

interface Props {
  schedulePool?: ScheduleEntry[];
}

interface Emits {
  (e: 'rowEdit', date: string): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const columns = [
  {
    title: '日期',
    dataIndex: 'date',
    width: 140,
    sorter: (a: ScheduleEntry, b: ScheduleEntry) => new Date(a.date).getTime() - new Date(b.date).getTime(),
  },
  {
    title: '星期',
    dataIndex: 'weekday',
    width: 80,
    align: 'center' as const,
  },
  { title: '安排', key: 'assignments' },
  { title: '备注', dataIndex: 'note', ellipsis: true },
  {
    title: '操作',
    width: 80,
    align: 'center' as const,
    key: 'action',
  },
];

const today = new Date().toISOString().split('T')[0];

const dataSource = computed(() =>
  (props.schedulePool || [])
    .map((entry) => ({
      ...entry,
      key: entry.date,
      weekday: new Date(entry.date + 'T00:00:00').toLocaleDateString('zh-CN', {
        weekday: 'short',
      }),
    }))
    .sort((a, b) => new Date(a.date).getTime() - new Date(b.date).getTime()),
);
</script>

<template>
  <div class="table-wrapper">
    <a-table
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
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.dataIndex === 'date'">
          <div
            class="date-cell"
            :class="{ 'date-cell--past': record.date < today }"
          >
            {{ record.date }}
          </div>
          <div class="weekday-cell">{{ record.weekday }}</div>
        </template>
        <template v-else-if="column.key === 'assignments'">
          <div v-if="Object.keys(record.area_assignments || {}).length > 0" class="assignments-wrap">
            <Tag
              v-for="(persons, area) in record.area_assignments"
              :key="area"
              color="blue"
              class="area-tag"
            >
              <span class="area-name">{{ area }}</span>
              <span class="area-persons">{{ (persons as string[]).join('、') }}</span>
            </Tag>
          </div>
          <span v-else class="no-assignment">暂无安排</span>
        </template>
        <template v-else-if="column.key === 'action'">
          <Tooltip title="编辑">
            <a-button
              size="small"
              type="text"
              @click="emit('rowEdit', record.date)"
            >
              <template #icon><EditOutlined /></template>
            </a-button>
          </Tooltip>
        </template>
      </template>
    </a-table>

    <div
      v-if="!props.schedulePool?.length"
      class="empty-state"
    >
      <div class="empty-state-icon">📋</div>
      <div class="empty-state-title">暂无排班数据</div>
      <div class="empty-state-desc">点击顶部「新建排班」添加</div>
    </div>
  </div>
</template>

<style scoped>
.table-wrapper {
  width: 100%;
}

:deep(.date-cell) {
  font-weight: 600;
  font-size: 13px;
  color: var(--da-text-primary);
}

:deep(.date-cell--past) {
  color: var(--da-text-muted);
}

:deep(.weekday-cell) {
  font-size: 11px;
  color: var(--da-text-secondary);
  margin-top: 2px;
}

.assignments-wrap {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.area-tag {
  border-radius: 4px;
  line-height: 1.4;
  display: inline-flex;
  flex-direction: column;
  align-items: flex-start;
  padding: 2px 8px !important;
  min-width: 80px;
}

.area-name {
  font-size: 11px;
  font-weight: 600;
  color: inherit;
}

.area-persons {
  font-size: 11px;
  opacity: 0.8;
}

.no-assignment {
  color: var(--da-text-muted);
  font-size: 12px;
}
</style>
