<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { Segmented, Spin, Space } from 'ant-design-vue';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import ScheduleCalendar from '@/components/schedule/ScheduleCalendar.vue';
import ScheduleTable from '@/components/schedule/ScheduleTable.vue';
import ArrangementEditor from '@/components/schedule/ArrangementEditor.vue';
import { api } from '@/api/http';
import type { Workspace } from '@/types';
import type { ScheduleEntry } from '@/types';

const viewMode = ref<'calendar' | 'table'>('calendar');
const editorOpen = ref(false);
const selectedDate = ref<string | null>(null);

const workspace = ref<Workspace | null>(null);
const loading = ref(false);

const refresh = async () => {
  loading.value = true;
  try {
    workspace.value = await api.getSnapshot();
  } finally {
    loading.value = false;
  }
};

onMounted(() => {
  refresh();
});

const handleDateSelect = (date: string) => {
  selectedDate.value = date;
  editorOpen.value = true;
};

const handleNewSchedule = () => {
  selectedDate.value = null;
  editorOpen.value = true;
};

const handleEditorSave = async (entry: ScheduleEntry) => {
  await api.saveScheduleEntry(entry);
  await refresh();
  editorOpen.value = false;
};
</script>

<template>
  <div class="page-container animate-fade-in">
    <!-- Page header -->
    <div class="page-header">
      <div>
        <h1 class="page-title">排班安排</h1>
        <p class="page-subtitle">
          <template v-if="workspace?.state?.schedule_pool?.length">
            共 {{ workspace.state.schedule_pool.length }} 条排班记录
          </template>
          <template v-else>查看和管理每日值日安排</template>
        </p>
      </div>
      <Space>
        <a-button @click="refresh" :loading="loading">
          <template #icon><ReloadOutlined /></template>
          刷新
        </a-button>
        <a-button type="primary" @click="handleNewSchedule">
          <template #icon><PlusOutlined /></template>
          新建排班
        </a-button>
      </Space>
    </div>

    <!-- View mode switcher -->
    <a-card :bordered="false" class="animate-fade-in-up" style="animation-delay: 0.05s">
      <div style="display: flex; justify-content: space-between; align-items: center">
        <Segmented
          v-model:value="viewMode"
          :options="[
            { label: '日历视图', value: 'calendar' },
            { label: '列表视图', value: 'table' },
          ]"
        />
        <span style="font-size: 13px; color: var(--da-text-secondary)">
          <template v-if="workspace?.state?.schedule_pool?.length">
            {{ workspace.state.schedule_pool.length }} 条记录
          </template>
        </span>
      </div>
    </a-card>

    <!-- Schedule View -->
    <div class="animate-fade-in-up" style="animation-delay: 0.1s">
      <Spin :spinning="loading">
        <ScheduleCalendar
          v-if="viewMode === 'calendar'"
          :schedule-pool="workspace?.state?.schedule_pool"
          @date-select="handleDateSelect"
        />
        <ScheduleTable
          v-else
          :schedule-pool="workspace?.state?.schedule_pool"
          @row-edit="handleDateSelect"
        />
      </Spin>
    </div>

    <!-- Arrangement Editor Drawer -->
    <ArrangementEditor
      v-model:open="editorOpen"
      :date="selectedDate"
      :schedule-pool="workspace?.state?.schedule_pool"
      :roster="workspace?.roster"
      @save="handleEditorSave"
    />
  </div>
</template>
