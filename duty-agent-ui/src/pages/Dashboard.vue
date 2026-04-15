<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { Row, Col, Alert } from 'ant-design-vue';
import {
  TeamOutlined,
  CalendarOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { api } from '@/api/http';
import type { Workspace } from '@/types';

const workspace = ref<Workspace | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);

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
});

const today = new Date().toISOString().split('T')[0];

onMounted(() => {
  refresh();
});

const stats = computed(() => {
  const roster = workspace.value?.roster ?? [];
  const pool = workspace.value?.state?.schedule_pool ?? [];
  const now = new Date();
  const weekStart = new Date(now);
  weekStart.setDate(now.getDate() - now.getDay());
  const weekEnd = new Date(weekStart);
  weekEnd.setDate(weekStart.getDate() + 7);

  return [
    {
      title: '总人数',
      value: roster.filter((r) => r.active).length,
      icon: TeamOutlined,
      iconColor: '#1890ff',
      color: '#e6f7ff',
      textColor: '#096dd9',
    },
    {
      title: '本周排班',
      value: pool.filter((s) => {
        const d = new Date(s.date);
        return d >= weekStart && d < weekEnd;
      }).length,
      icon: CalendarOutlined,
      iconColor: '#52c41a',
      color: '#f6ffed',
      textColor: '#389e0d',
    },
    {
      title: '已完成',
      value: pool.filter((s) => new Date(s.date) < new Date(today)).length,
      icon: CheckCircleOutlined,
      iconColor: '#faad14',
      color: '#fffbe6',
      textColor: '#d48806',
    },
    {
      title: '待排班',
      value: pool.filter((s) => new Date(s.date) >= new Date(today)).length,
      icon: ClockCircleOutlined,
      iconColor: '#722ed1',
      color: '#f9f0ff',
      textColor: '#531dab',
    },
  ];
});

const recentSchedule = computed(() =>
  (workspace.value?.state?.schedule_pool ?? [])
    .sort((a, b) => new Date(a.date).getTime() - new Date(b.date).getTime())
    .slice(0, 8),
);

const activeRoster = computed(() =>
  (workspace.value?.roster ?? []).filter((r) => r.active),
);
</script>

<template>
  <div class="page-container animate-fade-in">
    <!-- Page header -->
    <div class="page-header">
      <div>
        <h1 class="page-title">首页仪表盘</h1>
        <p class="page-subtitle">实时了解排班状态</p>
      </div>
      <a-button type="primary" :loading="loading" @click="refresh">
        <template #icon><ReloadOutlined /></template>
        刷新
      </a-button>
    </div>

    <!-- Stats row -->
    <Row :gutter="[16, 16]" class="stagger-children">
      <Col v-for="(stat, i) in stats" :key="i" :xs="12" :sm="12" :lg="6">
        <div class="stat-card hover-lift" :style="{ background: stat.color }">
          <component
            :is="stat.icon"
            :style="{ color: stat.iconColor, fontSize: '32px' }"
          />
          <div>
            <div class="stat-label">{{ stat.title }}</div>
            <div class="stat-value" :style="{ color: stat.textColor }">
              {{ stat.value }}
            </div>
          </div>
        </div>
      </Col>
    </Row>

    <!-- Main content row -->
    <Row :gutter="[16, 16]">
      <!-- Recent schedule -->
      <Col :xs="24" :lg="16">
        <a-card
          title="近期排班"
          :bordered="false"
          class="animate-fade-in-up"
          style="animation-delay: 0.15s"
        >
          <template #extra>
            <RouterLink to="/arrangement" style="font-size: 13px; color: var(--da-primary)">
              查看全部
            </RouterLink>
          </template>

          <div v-if="recentSchedule && recentSchedule.length > 0" class="schedule-list">
            <div
              v-for="(entry) in recentSchedule"
              :key="entry.date"
              class="schedule-item"
            >
              <div class="schedule-item-left">
                <div
                  class="schedule-date"
                  :class="{ 'schedule-date--past': new Date(entry.date) < new Date(today) }"
                >
                  {{ entry.date }}
                </div>
                <div class="schedule-day">
                  {{ new Date(entry.date + 'T00:00:00').toLocaleDateString('zh-CN', { weekday: 'short' }) }}
                </div>
              </div>
              <div class="schedule-item-right">
                <div
                  v-if="Object.keys(entry.area_assignments || {}).length > 0"
                  class="schedule-assignments"
                >
                  <span
                    v-for="(persons, area) in entry.area_assignments"
                    :key="area"
                    class="assignment-tag"
                  >
                    {{ area }}: {{ (persons as string[]).slice(0, 3).join(', ')
                    }}{{ (persons as string[]).length > 3 ? `等${(persons as string[]).length}人` : '' }}
                  </span>
                </div>
                <div v-else class="schedule-empty">暂无安排</div>
              </div>
            </div>
          </div>

          <div v-else class="empty-state">
            <div class="empty-state-icon">📅</div>
            <div class="empty-state-title">暂无排班数据</div>
            <div class="empty-state-desc">前往「排班安排」页面添加</div>
          </div>
        </a-card>
      </Col>

      <!-- Roster summary -->
      <Col :xs="24" :lg="8">
        <a-card
          title="花名册"
          :bordered="false"
          class="animate-fade-in-up"
          style="animation-delay: 0.2s"
        >
          <template #extra>
            <RouterLink to="/roster" style="font-size: 13px; color: var(--da-primary)">
              管理
            </RouterLink>
          </template>

          <div v-if="activeRoster && activeRoster.length > 0">
            <div class="roster-grid">
              <a-tag
                v-for="person in activeRoster.slice(0, 12)"
                :key="person.name"
                color="blue"
                class="roster-tag"
              >
                {{ person.name }}
              </a-tag>
              <div
                v-if="activeRoster.length > 12"
                class="roster-more"
              >
                还有 {{ activeRoster.length - 12 }} 人
              </div>
            </div>
          </div>

          <div v-else class="empty-state">
            <div class="empty-state-icon">👥</div>
            <div class="empty-state-title">暂无人员</div>
            <div class="empty-state-desc">前往「花名册管理」添加</div>
          </div>
        </a-card>
      </Col>
    </Row>

    <!-- Error alert -->
    <Alert
      v-if="error"
      type="warning"
      message="数据加载失败"
      :description="error"
      show-icon
      closable
    />
  </div>
</template>

<style scoped>
/* Schedule list */
.schedule-list {
  display: flex;
  flex-direction: column;
}

.schedule-item {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 10px 0;
  border-bottom: 1px solid var(--da-border);
  transition: background 0.15s ease;
  border-radius: 6px;
}

.schedule-item:last-child {
  border-bottom: none;
}

.schedule-item:hover {
  background: #fafafa;
  padding-left: 8px;
  padding-right: 8px;
  margin: 0 -16px;
}

.schedule-item-left {
  min-width: 110px;
  flex-shrink: 0;
}

.schedule-date {
  font-weight: 600;
  font-size: 13px;
  color: var(--da-text-primary);
}

.schedule-date--past {
  color: var(--da-text-secondary);
}

.schedule-day {
  font-size: 11px;
  color: var(--da-text-secondary);
  margin-top: 2px;
}

.schedule-item-right {
  flex: 1;
  min-width: 0;
}

.schedule-assignments {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.assignment-tag {
  background: #e6f7ff;
  color: #096dd9;
  border: 1px solid #91d5ff;
  border-radius: 4px;
  font-size: 12px;
  padding: 1px 6px;
}

.schedule-empty {
  font-size: 12px;
  color: var(--da-text-muted);
}

/* Roster grid */
.roster-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.roster-tag {
  border-radius: 20px;
  font-size: 12px;
  transition: var(--da-transition);
}

.roster-tag:hover {
  transform: scale(1.05);
}

.roster-more {
  width: 100%;
  font-size: 12px;
  color: var(--da-text-secondary);
  padding-top: 4px;
}
</style>
