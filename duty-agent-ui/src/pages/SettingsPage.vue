<script setup lang="ts">
import { ref, onMounted } from 'vue';
import {
  Input, Button, Space, Typography, Divider, Alert, message, Select, Switch, Slider, Tag,
} from 'ant-design-vue';
import {
  SaveOutlined, InfoCircleOutlined, ThunderboltOutlined,
} from '@ant-design/icons';
import AiToolsPanel from '@/components/ai-tools/AiToolsPanel.vue';
import { api } from '@/api/http';

const { Text } = Typography;
const activeTab = ref('ai-schedule');

// ======== 后端连接状态 ========
const backendConnected = ref(false);
const backendLoading = ref(false);

const checkBackend = async () => {
  backendLoading.value = true;
  try {
    await api.getSnapshot();
    backendConnected.value = true;
  } catch {
    backendConnected.value = false;
  } finally {
    backendLoading.value = false;
  }
};

// ======== AI 排班配置 ========
const dutyRule = ref('');
const dutyRuleSaved = ref(true);
const dutyRuleLoading = ref(false);

const planMode = ref('standard');
const planModeOptions = [
  { label: '标准', value: 'standard' },
  { label: 'Agents', value: 'agents' },
  { label: '增量小模型', value: 'incremental_small' },
];

const agentExecutionOrder = ref('auto');
const agentOrderOptions = [
  { label: '自动', value: 'auto' },
  { label: '并发执行', value: 'parallel' },
  { label: '串行执行', value: 'serial' },
];

const modelProfile = ref('auto');
const modelProfileOptions = [
  { label: '自动', value: 'auto' },
  { label: '云模型', value: 'cloud' },
  { label: '校内小模型', value: 'campus_small' },
  { label: '边缘模型', value: 'edge' },
  { label: '自定义', value: 'custom' },
];

onMounted(async () => {
  await checkBackend();
  try {
    const config = await api.getConfig();
    dutyRule.value = config.duty_rule || '';
    planMode.value = config.orchestration_mode || 'standard';
    agentExecutionOrder.value = config.multi_agent_execution_mode || 'auto';
    modelProfile.value = config.model_profile || 'auto';
    dutyRuleSaved.value = true;
  } catch (e) {
    // ignore
  }
});

const saveDutyRule = async () => {
  dutyRuleLoading.value = true;
  try {
    await api.updateConfig({ duty_rule: dutyRule.value });
    dutyRuleSaved.value = true;
    message.success('长期规则已保存');
  } catch (e) {
    message.error('保存失败');
  } finally {
    dutyRuleLoading.value = false;
  }
};

const handleDutyRuleChange = () => {
  dutyRuleSaved.value = false;
};

// ======== 自动运行配置 ========
const autoRunEnabled = ref(false);
const autoRunMode = ref('weekly');
const autoRunDay = ref('1');
const autoRunHour = ref(8);
const autoRunMinute = ref(0);
const triggerNotification = ref(true);

const autoRunModeOptions = [
  { label: '每周', value: 'weekly' },
  { label: '每月', value: 'monthly' },
  { label: '自定义间隔', value: 'interval' },
];

const weekDays = [
  { label: '周一', value: '1' },
  { label: '周二', value: '2' },
  { label: '周三', value: '3' },
  { label: '周四', value: '4' },
  { label: '周五', value: '5' },
  { label: '周六', value: '6' },
  { label: '周日', value: '0' },
];

// ======== 通知设置 ========
const reminderEnabled = ref(false);
const reminderTimes = ref('07:40, 12:10');
const notificationDuration = ref(8);
</script>

<template>
  <div class="page-container animate-fade-in">
    <!-- Page header -->
    <div class="page-header">
      <div>
        <h1 class="page-title">设置</h1>
        <p class="page-subtitle">配置 AI 工具、排班规则和其他选项</p>
      </div>
    </div>

    <a-tabs v-model:activeKey="activeTab">

      <!-- ======== AI 排班 ======== -->
      <a-tabs-tab-pane key="ai-schedule" tab="AI 排班">
        <div class="tab-content">

          <!-- 后端连接状态 -->
          <a-card class="mb-16">
            <div class="backend-status-row">
              <div class="backend-status-left">
                <div style="display: flex; align-items: center; gap: 8px">
                  <Tag :color="backendConnected ? 'green' : 'red'" style="border-radius: 12px; margin: 0">
                    {{ backendConnected ? '● 已连接' : '○ 未连接' }}
                  </Tag>
                  <span v-if="backendConnected" style="font-size: 13px; color: var(--da-text-secondary)">
                    令牌由 ClassIsland 插件自动注入
                  </span>
                  <span v-else style="font-size: 13px; color: var(--da-text-secondary)">
                    请确保 ClassIsland 插件已启动
                  </span>
                </div>
              </div>
              <Button size="small" :loading="backendLoading" @click="checkBackend">
                <template #icon><ThunderboltOutlined /></template>
                检测连接
              </Button>
            </div>
          </a-card>

          <!-- AI 长期规则 -->
          <a-card class="mb-16">
            <template #title>
              <div class="card-title-row">
                <span>📋 AI 长期规则</span>
                <Tag color="blue" size="small">全局</Tag>
              </div>
            </template>
            <template #extra>
              <Button
                type="primary"
                size="small"
                :loading="dutyRuleLoading"
                :disabled="dutyRuleSaved"
                @click="saveDutyRule"
              >
                <template #icon><SaveOutlined /></template>
                {{ dutyRuleSaved ? '已保存' : '保存规则' }}
              </Button>
            </template>

            <div class="duty-rule-hint">
              <InfoCircleOutlined style="color: #1890ff; margin-right: 6px" />
              作为固定要求附加到每次排班请求，无需每次手动填写
            </div>

            <Input.TextArea
              v-model:value="dutyRule"
              :rows="5"
              :maxlength="2000"
              show-count
              placeholder="示例：
每天排班 2 人，跳过周末
区域：教室、清洁区
教室每天 2 人，清洁区每天 2 人
排班周期：7 天"
              @input="handleDutyRuleChange"
              style="font-family: 'Courier New', monospace; font-size: 13px"
            />
          </a-card>

          <!-- 排班方案预设 -->
          <a-card>
            <template #title>排班方案预设</template>
            <template #extra>
              <Space>
                <Button size="small">新建方案</Button>
                <Button size="small">复制当前</Button>
              </Space>
            </template>

            <Alert
              message="方案预设"
              description="预设包含模型、执行模式等配置。切换方案后，后续排班将使用新方案。"
              type="info"
              show-icon
              style="margin-bottom: 16px"
            />

            <div class="config-grid">
              <!-- 执行模式 -->
              <div class="config-item">
                <div class="config-label">执行模式</div>
                <div class="config-desc">决定排班的执行方式</div>
                <Select
                  v-model:value="planMode"
                  :options="planModeOptions"
                  style="width: 180px; margin-top: 6px"
                  size="small"
                />
                <div class="config-mode-hint">
                  <template v-if="planMode === 'standard'">
                    标准模式：适用于大多数场景，简单高效
                  </template>
                  <template v-else-if="planMode === 'agents'">
                    Agents 模式：多 Agent 协作，适用于复杂规则
                  </template>
                  <template v-else>
                    增量小模型：本地小模型增量更新，节省资源
                  </template>
                </div>
              </div>

              <Divider />

              <!-- Agents 执行顺序 -->
              <div class="config-item" v-if="planMode === 'agents'">
                <div class="config-label">Agents 执行顺序</div>
                <div class="config-desc">仅对 Agents 方案生效</div>
                <Select
                  v-model:value="agentExecutionOrder"
                  :options="agentOrderOptions"
                  style="width: 180px; margin-top: 6px"
                  size="small"
                />
              </div>

              <!-- 模型画像 -->
              <div class="config-item">
                <div class="config-label">模型画像</div>
                <div class="config-desc">帮助后端选择更合适的提示词组织方式</div>
                <Select
                  v-model:value="modelProfile"
                  :options="modelProfileOptions"
                  style="width: 180px; margin-top: 6px"
                  size="small"
                />
              </div>
            </div>
          </a-card>
        </div>
      </a-tabs-tab-pane>

      <!-- ======== AI 工具 ======== -->
      <a-tabs-tab-pane key="ai-tools" tab="AI 工具">
        <AiToolsPanel />
      </a-tabs-tab-pane>

      <!-- ======== 自动运行 ======== -->
      <a-tabs-tab-pane key="auto-run" tab="自动运行">
        <div class="tab-content">

          <!-- 自动排班 -->
          <a-card class="mb-16">
            <template #title>
              <div class="card-title-row">
                <span>⏰ 自动排班</span>
                <Switch v-model:checked="autoRunEnabled" size="small" />
              </div>
            </template>
            <template #extra>
              <Text type="secondary" style="font-size: 12px">
                {{ autoRunEnabled ? '已启用' : '已关闭' }}
              </Text>
            </template>

            <div class="auto-run-grid">
              <div class="config-item">
                <div class="config-label">触发策略</div>
                <div class="config-desc">选择自动排班的执行频率</div>
                <Select
                  v-model:value="autoRunMode"
                  :options="autoRunModeOptions"
                  style="width: 160px; margin-top: 6px"
                  size="small"
                />
              </div>

              <div class="config-item" v-if="autoRunMode === 'weekly'">
                <div class="config-label">每周日期</div>
                <div class="config-desc">选择在每周哪一天执行</div>
                <Select
                  v-model:value="autoRunDay"
                  :options="weekDays"
                  style="width: 120px; margin-top: 6px"
                  size="small"
                />
              </div>

              <div class="config-item">
                <div class="config-label">执行时间</div>
                <div class="config-desc">一天中自动排班的执行时刻</div>
                <Space style="margin-top: 6px" size="small">
                  <Select
                    v-model:value="autoRunHour"
                    :options="Array.from({ length: 24 }, (_, i) => ({ label: String(i).padStart(2, '0') + ' 时', value: i }))"
                    style="width: 80px"
                    size="small"
                  />
                  <Select
                    v-model:value="autoRunMinute"
                    :options="Array.from({ length: 12 }, (_, i) => ({ label: String(i * 5).padStart(2, '0') + ' 分', value: i * 5 }))"
                    style="width: 80px"
                    size="small"
                  />
                </Space>
              </div>

              <div class="config-item">
                <div class="config-label">触发时通知</div>
                <div class="config-desc">自动排班启动时发送一条通知</div>
                <Switch v-model:checked="triggerNotification" style="margin-top: 6px" />
              </div>
            </div>
          </a-card>

          <!-- 值日提醒 -->
          <a-card>
            <template #title>
              <div class="card-title-row">
                <span>🔔 值日提醒</span>
                <Switch v-model:checked="reminderEnabled" size="small" />
              </div>
            </template>
            <template #extra>
              <Text type="secondary" style="font-size: 12px">
                {{ reminderEnabled ? '已启用' : '已关闭' }}
              </Text>
            </template>

            <div class="auto-run-grid">
              <div class="config-item" style="grid-column: 1 / -1">
                <div class="config-label">提醒时间</div>
                <div class="config-desc">控制当前值日安排通知的提醒时刻，多个时间用逗号分隔</div>
                <Input
                  v-model:value="reminderTimes"
                  placeholder="例如：07:40, 12:10, 16:30"
                  style="max-width: 320px; margin-top: 6px"
                  size="small"
                />
              </div>

              <div class="config-item">
                <div class="config-label">显示时长</div>
                <div class="config-desc">提醒在屏幕上的停留时间</div>
                <Space style="margin-top: 6px" align="center">
                  <Slider
                    v-model:value="notificationDuration"
                    :min="3"
                    :max="15"
                    :marks="{ 3: '3秒', 8: '8秒', 15: '15秒' }"
                    style="width: 200px"
                  />
                  <Tag color="blue" size="small">{{ notificationDuration }} 秒</Tag>
                </Space>
              </div>
            </div>
          </a-card>
        </div>
      </a-tabs-tab-pane>

      <!-- ======== 通知设置 ======== -->
      <a-tabs-tab-pane key="notification" tab="通知设置">
        <a-card>
          <template #title>通知设置</template>
          <div class="empty-state">
            <div class="empty-state-icon">🔔</div>
            <div class="empty-state-title">通知设置</div>
            <div class="empty-state-desc">配置排班通知和提醒方式（待实现）</div>
          </div>
        </a-card>
      </a-tabs-tab-pane>

    </a-tabs>
  </div>
</template>

<style scoped>
.tab-content {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.mb-16 {
  margin-bottom: 16px;
}

/* Backend status */
.backend-status-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.backend-status-left {
  flex: 1;
}

/* Duty rule */
.duty-rule-hint {
  font-size: 12px;
  color: var(--da-text-secondary);
  background: #e6f7ff;
  border: 1px solid #91d5ff;
  border-radius: 6px;
  padding: 8px 12px;
  margin-bottom: 12px;
  line-height: 1.5;
}

/* Card title row */
.card-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
}

/* Config grid */
.config-grid {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.config-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.config-label {
  font-size: 13px;
  font-weight: 600;
  color: var(--da-text-primary);
}

.config-desc {
  font-size: 12px;
  color: var(--da-text-secondary);
}

.config-mode-hint {
  font-size: 12px;
  color: var(--da-text-secondary);
  background: #fafafa;
  border-radius: 4px;
  padding: 4px 8px;
  margin-top: 6px;
  border-left: 3px solid #1890ff;
}

/* Auto run grid */
.auto-run-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 20px;
}

@media (max-width: 640px) {
  .auto-run-grid {
    grid-template-columns: 1fr;
  }
}
</style>
