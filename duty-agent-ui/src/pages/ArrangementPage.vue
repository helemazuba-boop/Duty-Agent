<script setup lang="ts">
import { ref, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { Segmented, Spin, Space } from 'ant-design-vue';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons-vue';
import ScheduleCalendar from '@/components/schedule/ScheduleCalendar.vue';
import ScheduleTable from '@/components/schedule/ScheduleTable.vue';
import ArrangementEditor from '@/components/schedule/ArrangementEditor.vue';
import PageHeader from '@/components/ui/PageHeader.vue';
import { api } from '@/api/http';
import type { Workspace, ScheduleEntry } from '@/types';

type ViewMode = 'calendar' | 'table';

const route = useRoute();
const router = useRouter();

/** 视图状态记入 query,刷新不丢 */
const readViewMode = (): ViewMode => (route.query.view === 'table' ? 'table' : 'calendar');
const viewMode = ref<ViewMode>(readViewMode());

watch(
  () => route.query.view,
  () => {
    viewMode.value = readViewMode();
  },
);

const switchView = (value: ViewMode) => {
  viewMode.value = value;
  router.replace({ query: { ...route.query, view: value === 'table' ? 'table' : undefined } });
};

const editorOpen = ref(false);
const selectedDate = ref<string | null>(null);

const workspace = ref<Workspace | null>(null);
const loading = ref(false);
const updatedAt = ref(0);

const refresh = async () => {
  loading.value = true;
  try {
    workspace.value = await api.getSnapshot();
    updatedAt.value = Date.now();
  } catch (e) {
    console.error('[ArrangementPage] refresh failed:', e);
    workspace.value = null;
  } finally {
    loading.value = false;
  }
};

onMounted(() => {
  refresh();
});

const pool = () => workspace.value?.state?.schedule_pool;

const handleDateSelect = (date: string) => {
  selectedDate.value = date;
  editorOpen.value = true;
};

const handleNewSchedule = () => {
  selectedDate.value = null;
  editorOpen.value = true;
};

const handleEditorSave = async (entry: ScheduleEntry) => {
  try {
    await api.saveScheduleEntry(entry);
    await refresh();
    editorOpen.value = false;
  } catch (e) {
    console.error('[ArrangementPage] save failed:', e);
  }
};
</script>

<template>
  <div class="page-container animate-fade-in">
    <PageHeader
      title="排班安排"
      :subtitle="pool()?.length ? `共 ${pool()!.length} 条排班记录` : '查看和管理每日值日安排'"
    >
      <template #actions>
        <Space>
          <Segmented
            :value="viewMode"
            :options="[
              { label: '日历', value: 'calendar' },
              { label: '列表', value: 'table' },
            ]"
            @change="(v: any) => switchView(v as ViewMode)"
          />
          <a-button @click="refresh" :loading="loading">
            <template #icon><ReloadOutlined /></template>
          </a-button>
          <a-button type="primary" @click="handleNewSchedule">
            <template #icon><PlusOutlined /></template>
            新建排班
          </a-button>
        </Space>
      </template>
    </PageHeader>

    <Spin :spinning="loading">
      <div v-flash="updatedAt">
        <ScheduleCalendar
          v-if="viewMode === 'calendar'"
          :schedule-pool="pool()"
          @date-select="handleDateSelect"
        />
        <ScheduleTable
          v-else
          :schedule-pool="pool()"
          @row-edit="handleDateSelect"
        />
      </div>
    </Spin>

    <ArrangementEditor
      v-model:open="editorOpen"
      :date="selectedDate"
      :schedule-pool="pool()"
      :roster="workspace?.roster"
      @save="handleEditorSave"
    />
  </div>
</template>
