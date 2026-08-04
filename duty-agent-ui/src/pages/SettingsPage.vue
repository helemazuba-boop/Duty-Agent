<script setup lang="ts">
import { computed, ref, onMounted } from 'vue';
import {
  Input, Button, Space, Divider, Alert, message, Select, Switch, Slider, Tag, InputNumber,
} from 'ant-design-vue';
import {
  SaveOutlined, InfoCircleOutlined, ThunderboltOutlined,
} from '@ant-design/icons-vue';
import AiToolsPanel from '@/components/ai-tools/AiToolsPanel.vue';
import { api, type NotificationSettings } from '@/api/http';

const activeTab = ref('ai-schedule');

// ======== 连接状态 ========
const backendConnected = ref(false);
const backendLoading = ref(false);
const bridgeConnected = ref(false);
const bridgeLoading = ref(false);

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

const checkBridge = async () => {
  bridgeLoading.value = true;
  try {
    const status = await api.getBridgeStatus();
    bridgeConnected.value = status.connected && status.status === 'connected';
  } catch {
    bridgeConnected.value = false;
  } finally {
    bridgeLoading.value = false;
  }
};

const checkConnections = async () => {
  await Promise.allSettled([checkBackend(), checkBridge()]);
};

// ======== 后端配置（方案 / 规则） ========
const dutyRule = ref('');
const dutyRuleSaved = ref(true);
const dutyRuleLoading = ref(false);

const configVersion = ref<number | null>(null);
const planPresets = ref<any[]>([]);
const selectedPlanId = ref('standard');
const orchestrationMode = ref('');
const agentExecutionOrder = ref('auto');
const modelProfile = ref('auto');
const singlePassStrategy = ref('auto');
const offlineScheduleDays = ref(7);
const offlineSkipWeekends = ref(true);
const planDirty = ref(false);
const planSaving = ref(false);
const planProbing = ref(false);

// 后端 preset id 为 'incremental-small'（连字符）；labels 仅作 name 缺失时的回退。
const planLabels: Record<string, string> = {
  standard: '标准',
  agents: 'Agents',
  'incremental-small': '增量小模型',
  offline: '离线算法',
};

// 后端水合后的 orchestration_mode 为 single_pass / multi_agent / offline。
const modeLabels: Record<string, string> = {
  single_pass: '单轮执行',
  multi_agent: '多 Agent',
  offline: '离线算法（无需模型）',
};

const agentOrderLabels: Record<string, string> = {
  auto: '自动',
  parallel: '并发执行',
  serial: '串行执行',
};

const modelProfileLabels: Record<string, string> = {
  auto: '自动',
  cloud: '云模型',
  campus_small: '校内小模型',
  edge: '边缘模型',
  custom: '自定义',
};

const modelProfileOptions = Object.entries(modelProfileLabels).map(([value, label]) => ({ value, label }));
const agentOrderOptions = Object.entries(agentOrderLabels).map(([value, label]) => ({ value, label }));

const currentPreset = computed(() => planPresets.value.find((p) => p.id === selectedPlanId.value) || null);
const isOfflinePreset = computed(() => currentPreset.value?.mode_id === 'offline');
const planOptions = computed(() => planPresets.value.map((p) => ({
  value: p.id,
  label: p.name || planLabels[p.id] || p.id,
})));

const currentPlanLabel = computed(
  () => currentPreset.value?.name || planLabels[selectedPlanId.value] || selectedPlanId.value || '标准',
);
const currentModeLabel = computed(() => modeLabels[orchestrationMode.value] || orchestrationMode.value || '—');
const currentAgentOrderLabel = computed(() => agentOrderLabels[agentExecutionOrder.value] || agentExecutionOrder.value || '—');
const currentModelProfileLabel = computed(() => modelProfileLabels[modelProfile.value] || modelProfile.value || '—');

const applyConfig = (config: any, opts: { keepDutyRuleDraft?: boolean } = {}) => {
  configVersion.value = typeof config.version === 'number' ? config.version : null;
  planPresets.value = Array.isArray(config.plan_presets)
    ? JSON.parse(JSON.stringify(config.plan_presets))
    : [];
  selectedPlanId.value = config.selected_plan_id || 'standard';
  orchestrationMode.value = config.orchestration_mode || '';
  agentExecutionOrder.value = config.multi_agent_execution_mode || 'auto';
  modelProfile.value = config.model_profile || 'auto';
  singlePassStrategy.value = config.single_pass_strategy || 'auto';
  offlineScheduleDays.value = typeof config.offline_schedule_days === 'number' ? config.offline_schedule_days : 7;
  offlineSkipWeekends.value = config.offline_skip_weekends !== false;
  if (!opts.keepDutyRuleDraft) {
    dutyRule.value = config.duty_rule || '';
    dutyRuleSaved.value = true;
  }
};

const loadConfig = async () => {
  try {
    applyConfig(await api.getConfig());
  } catch {
    // 后端未连接时保持本地默认
  }
};

const markPlanDirty = () => {
  planDirty.value = true;
};

const savePlanConfig = async () => {
  planSaving.value = true;
  try {
    const config = await api.updateConfig({
      expected_version: configVersion.value ?? undefined,
      selected_plan_id: selectedPlanId.value,
      plan_presets: planPresets.value,
      offline_schedule_days: offlineScheduleDays.value,
      offline_skip_weekends: offlineSkipWeekends.value,
    });
    applyConfig(config, { keepDutyRuleDraft: true });
    planDirty.value = false;
    message.success('方案已保存');
  } catch (e: any) {
    if (e?.response?.status === 409) {
      await loadConfig();
      planDirty.value = false;
      message.warning('方案已被其他入口更新，已重新加载，请确认后再保存');
    } else {
      message.error('方案保存失败');
    }
  } finally {
    planSaving.value = false;
  }
};

const testPlanConnection = async () => {
  const preset = currentPreset.value;
  if (!preset || !String(preset.base_url || '').trim() || !String(preset.model || '').trim()) {
    message.error('请先填写模型服务地址与模型名称');
    return;
  }
  planProbing.value = true;
  try {
    const r = await api.probeModel({ base_url: preset.base_url, model: preset.model, api_key: preset.api_key || '' });
    if (r.ok) message.success(r.detail || '连接成功');
    else message.warning(`${r.detail}${r.fix ? ' — ' + r.fix : ''}`);
  } catch {
    message.error('探测请求失败');
  } finally {
    planProbing.value = false;
  }
};

const saveDutyRule = async () => {
  dutyRuleLoading.value = true;
  try {
    const config = await api.updateConfig({ duty_rule: dutyRule.value });
    applyConfig(config);
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

// ======== 通知设置 ========
const triggerNotification = ref(true);
const notificationSettingsVersion = ref<number | null>(null);
const reminderEnabled = ref(false);
const reminderTimes = ref('07:40, 12:10');
const notificationDuration = ref(8);
const notificationEntry = ref<'system' | 'classisland' | 'both' | 'off'>('system');
const systemNotificationsEnabled = ref(true);
const scheduleCompletionNotificationEnabled = ref(true);
const notificationSettingsLoading = ref(false);

// ======== 系统与自启（独立客户端生命周期） ========
const clientAutoStart = ref(true);
const clientCloseAction = ref<'ask' | 'tray' | 'exit'>('ask');
const systemSettingsSaving = ref(false);

const CLOSE_ACTION_OPTIONS = [
  { value: 'ask', label: '每次询问' },
  { value: 'tray', label: '驻留后台（托盘）' },
  { value: 'exit', label: '退出程序' },
];

const notificationEntryOptions = [
  { label: '系统通知', value: 'system' },
  { label: 'ClassIsland', value: 'classisland' },
  { label: '两者', value: 'both' },
  { label: '关闭', value: 'off' },
];

const TIME_PATTERN = /^([01]?\d|2[0-3]):[0-5]\d$/;

const normalizeReminderTimes = () => reminderTimes.value
  .split(/[,;，；\r\n]+/)
  .map((item) => item.trim())
  .filter(Boolean);

// ======== 自动排班 ========
const WEEKDAY_OPTIONS = [
  { value: 'Monday', label: '周一' },
  { value: 'Tuesday', label: '周二' },
  { value: 'Wednesday', label: '周三' },
  { value: 'Thursday', label: '周四' },
  { value: 'Friday', label: '周五' },
  { value: 'Saturday', label: '周六' },
  { value: 'Sunday', label: '周日' },
];

const MONTH_DAY_OPTIONS = [
  ...Array.from({ length: 31 }, (_, i) => ({ value: String(i + 1), label: `${i + 1} 号` })),
  { value: 'L', label: '月末最后一天' },
];

const AUTO_RUN_MODE_OPTIONS = [
  { value: 'Off', label: '关闭' },
  { value: 'Weekly', label: '每周' },
  { value: 'Monthly', label: '每月' },
  { value: 'Custom', label: '自定义间隔' },
];

const autoRunMode = ref<'Off' | 'Weekly' | 'Monthly' | 'Custom'>('Off');
const weeklyDay = ref('Monday');
const monthDay = ref('L');
const customInterval = ref<number>(14);
const autoRunTime = ref('08:00');
const autoRunRetry = ref<number>(3);
const autoRunSaving = ref(false);

const autoRunParameterOut = computed(() => {
  if (autoRunMode.value === 'Weekly') return weeklyDay.value;
  if (autoRunMode.value === 'Monthly') return monthDay.value;
  if (autoRunMode.value === 'Custom') return String(customInterval.value || 14);
  return weeklyDay.value;
});

const applyNotificationSettings = (settings: NotificationSettings) => {
  notificationSettingsVersion.value = settings.version;
  notificationEntry.value = settings.notification_entry;
  systemNotificationsEnabled.value = settings.system_notifications_enabled;
  scheduleCompletionNotificationEnabled.value = settings.schedule_completion_notification_enabled;
  triggerNotification.value = settings.auto_run_trigger_notification_enabled;
  reminderEnabled.value = settings.duty_reminder_enabled;
  reminderTimes.value = (settings.duty_reminder_times || []).join(', ');
  notificationDuration.value = settings.notification_duration_seconds || 8;
  clientAutoStart.value = settings.client_auto_start !== false;
  clientCloseAction.value = (['ask', 'tray', 'exit'] as const).includes(settings.client_close_action as any)
    ? settings.client_close_action
    : 'ask';

  autoRunMode.value = (settings.auto_run_mode as any) || 'Off';
  autoRunTime.value = settings.auto_run_time || '08:00';
  autoRunRetry.value = settings.auto_run_retry_times ?? 3;
  const param = (settings.auto_run_parameter || '').trim();
  if (autoRunMode.value === 'Weekly') {
    const match = WEEKDAY_OPTIONS.find((o) => o.value.toLowerCase() === param.toLowerCase());
    weeklyDay.value = match ? match.value : 'Monday';
  } else if (autoRunMode.value === 'Monthly') {
    weeklyDay.value = 'Monday';
    monthDay.value = MONTH_DAY_OPTIONS.some((o) => o.value === param.toUpperCase())
      ? param.toUpperCase()
      : (MONTH_DAY_OPTIONS.some((o) => o.value === param) ? param : 'L');
  } else if (autoRunMode.value === 'Custom') {
    const parsed = parseInt(param, 10);
    customInterval.value = Number.isFinite(parsed) && parsed >= 1 ? parsed : 14;
  }
};

const loadNotificationSettings = async () => {
  try {
    applyNotificationSettings(await api.getNotificationSettings());
  } catch {
    // keep local defaults
  }
};

const saveNotificationSettings = async () => {
  const times = normalizeReminderTimes();
  const invalid = times.filter((t) => !TIME_PATTERN.test(t));
  if (invalid.length > 0) {
    message.error(`提醒时间格式不正确（应为 HH:MM）：${invalid.join('、')}`);
    return;
  }
  notificationSettingsLoading.value = true;
  try {
    const settings = await api.updateNotificationSettings({
      expected_version: notificationSettingsVersion.value ?? undefined,
      notification_entry: notificationEntry.value,
      system_notifications_enabled: systemNotificationsEnabled.value,
      schedule_completion_notification_enabled: scheduleCompletionNotificationEnabled.value,
      auto_run_trigger_notification_enabled: triggerNotification.value,
      duty_reminder_enabled: reminderEnabled.value,
      duty_reminder_times: times,
      notification_duration_seconds: notificationDuration.value,
    });
    applyNotificationSettings(settings);
    message.success('通知设置已保存');
  } catch (e: any) {
    if (e?.response?.status === 409) {
      await loadNotificationSettings();
      message.warning('通知设置已被其他入口更新，请确认后再保存');
    } else {
      message.error('通知设置保存失败');
    }
  } finally {
    notificationSettingsLoading.value = false;
  }
};

const saveAutoRunSettings = async () => {
  const time = autoRunTime.value.trim();
  if (!TIME_PATTERN.test(time)) {
    message.error('执行时间格式不正确，应为 HH:MM，例如 08:00');
    return;
  }
  autoRunSaving.value = true;
  try {
    const settings = await api.updateNotificationSettings({
      expected_version: notificationSettingsVersion.value ?? undefined,
      auto_run_mode: autoRunMode.value,
      auto_run_parameter: autoRunParameterOut.value,
      auto_run_time: time,
      auto_run_retry_times: autoRunRetry.value,
    });
    applyNotificationSettings(settings);
    message.success('自动排班设置已保存');
  } catch (e: any) {
    if (e?.response?.status === 409) {
      await loadNotificationSettings();
      message.warning('设置已被其他入口更新，请确认后再保存');
    } else {
      message.error('自动排班设置保存失败');
    }
  } finally {
    autoRunSaving.value = false;
  }
};

const sendTestNotification = async () => {
  try {
    await api.testNotification();
    message.success('测试通知已发送');
  } catch {
    message.error('测试通知发送失败');
  }
};

const saveSystemSettings = async () => {
  systemSettingsSaving.value = true;
  try {
    const settings = await api.updateNotificationSettings({
      expected_version: notificationSettingsVersion.value ?? undefined,
      client_auto_start: clientAutoStart.value,
      client_close_action: clientCloseAction.value,
    });
    applyNotificationSettings(settings);
    message.success('系统设置已保存，客户端将自动同步');
  } catch (e: any) {
    if (e?.response?.status === 409) {
      await loadNotificationSettings();
      message.warning('设置已被其他入口更新，请确认后再保存');
    } else {
      message.error('系统设置保存失败');
    }
  } finally {
    systemSettingsSaving.value = false;
  }
};

onMounted(async () => {
  await checkConnections();
  await Promise.allSettled([loadNotificationSettings(), loadConfig()]);
});
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
      <a-tab-pane key="ai-schedule" tab="AI 排班">
        <div class="tab-content">

          <!-- 连接状态 -->
          <a-card class="mb-16">
            <div class="connection-status-row">
              <div class="connection-status-list">
                <div class="connection-status-item">
                  <Tag :color="backendConnected ? 'green' : 'red'" style="border-radius: 12px; margin: 0">
                    {{ backendConnected ? '● 后端已启动' : '○ 后端未连接' }}
                  </Tag>
                  <span v-if="backendConnected" class="status-hint">令牌由独立客户端注入</span>
                  <span v-else class="status-hint">请先启动 Duty-Agent 独立客户端</span>
                </div>
                <div class="connection-status-item">
                  <Tag :color="bridgeConnected ? 'green' : 'default'" style="border-radius: 12px; margin: 0">
                    {{ bridgeConnected ? '● ClassIsland 已连接' : '○ ClassIsland 未连接' }}
                  </Tag>
                  <span v-if="bridgeConnected" class="status-hint">Duty-Agent-ClassIsland-Bridge 正在同步</span>
                  <span v-else class="status-hint">未检测到 ClassIsland Bridge，ClassIsland 未启动或桥接插件未连接</span>
                </div>
              </div>
              <Button size="small" :loading="backendLoading || bridgeLoading" @click="checkConnections">
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

          <!-- 方案与模型 -->
          <a-card class="mb-16">
            <template #title>
              <div class="card-title-row">
                <span>🧠 方案与模型</span>
              </div>
            </template>
            <template #extra>
              <Space>
                <Button
                  size="small"
                  :loading="planProbing"
                  @click="testPlanConnection"
                >
                  测试连接
                </Button>
                <Button
                  type="primary"
                  size="small"
                  :loading="planSaving"
                  :disabled="!planDirty"
                  @click="savePlanConfig"
                >
                  <template #icon><SaveOutlined /></template>
                  {{ planDirty ? '保存方案' : '已保存' }}
                </Button>
              </Space>
            </template>

            <div class="form-grid">
              <div class="config-item">
                <div class="config-label">当前方案</div>
                <div class="config-desc">切换后端使用的执行方案</div>
                <Select
                  v-model:value="selectedPlanId"
                  :options="planOptions"
                  class="config-control"
                  @change="markPlanDirty"
                />
              </div>

              <div class="config-item">
                <div class="config-label">当前执行画像</div>
                <div class="config-desc">由所选方案自动派生，只读</div>
                <div class="status-tags">
                  <Tag color="blue">{{ currentPlanLabel }}</Tag>
                  <Tag>{{ currentModeLabel }}</Tag>
                  <Tag>{{ currentModelProfileLabel }}</Tag>
                  <Tag v-if="orchestrationMode === 'multi_agent'">{{ currentAgentOrderLabel }}</Tag>
                  <Tag>{{ singlePassStrategy }}</Tag>
                </div>
              </div>
            </div>

            <Divider style="margin: 16px 0" />

            <template v-if="currentPreset">
              <div v-if="!isOfflinePreset" class="form-grid">
                <div class="config-item full-row">
                  <div class="config-label">模型服务地址 (base_url)</div>
                  <div class="config-desc">OpenAI 兼容端点，例如 https://api.example.com/v1 或本地 http://localhost:1234/v1</div>
                  <Input
                    v-model:value="currentPreset.base_url"
                    placeholder="https://integrate.api.nvidia.com/v1"
                    class="config-control"
                    @input="markPlanDirty"
                  />
                </div>

                <div class="config-item">
                  <div class="config-label">模型名称 (model)</div>
                  <Input
                    v-model:value="currentPreset.model"
                    placeholder="moonshotai/kimi-k2-thinking"
                    class="config-control"
                    @input="markPlanDirty"
                  />
                </div>

                <div class="config-item">
                  <div class="config-label">API Key</div>
                  <div class="config-desc">本地模型可留空</div>
                  <Input.Password
                    v-model:value="currentPreset.api_key"
                    placeholder="留空表示无需鉴权"
                    class="config-control"
                    @input="markPlanDirty"
                  />
                </div>

                <div class="config-item">
                  <div class="config-label">模型画像</div>
                  <Select
                    v-model:value="currentPreset.model_profile"
                    :options="modelProfileOptions"
                    class="config-control"
                    @change="markPlanDirty"
                  />
                </div>

                <div v-if="currentPreset.mode_id === 'agents'" class="config-item">
                  <div class="config-label">Agents 执行顺序</div>
                  <Select
                    v-model:value="currentPreset.multi_agent_execution_mode"
                    :options="agentOrderOptions"
                    class="config-control"
                    @change="markPlanDirty"
                  />
                </div>
              </div>

              <div v-else class="form-grid">
                <div class="config-item full-row">
                  <Alert
                    type="info"
                    show-icon
                    message="离线算法模式：无需模型服务"
                    description="不调用任何大模型，按花名册轮转 + 欠账/存欠公平规则本地生成排班。适合无模型或希望长期零依赖运行的场景。"
                  />
                </div>
                <div class="config-item">
                  <div class="config-label">排班天数</div>
                  <div class="config-desc">每次生成未来多少天（1–60）</div>
                  <InputNumber
                    v-model:value="offlineScheduleDays"
                    :min="1"
                    :max="60"
                    class="config-control"
                    @change="markPlanDirty"
                  />
                </div>
                <div class="config-item">
                  <div class="config-label">跳过周末</div>
                  <div class="config-desc">仅安排工作日（周一至周五）</div>
                  <Switch v-model:checked="offlineSkipWeekends" @change="markPlanDirty" />
                </div>
              </div>
            </template>
            <Alert
              v-else
              message="尚未加载到方案配置"
              description="后端未连接或配置为空。请先启动独立客户端后点击上方“检测连接”。"
              type="warning"
              show-icon
            />
          </a-card>

          <!-- 自动排班 -->
          <a-card>
            <template #title>
              <div class="card-title-row">
                <span>⏰ 自动排班</span>
              </div>
            </template>
            <template #extra>
              <Button
                type="primary"
                size="small"
                :loading="autoRunSaving"
                @click="saveAutoRunSettings"
              >
                <template #icon><SaveOutlined /></template>
                保存
              </Button>
            </template>

            <div class="duty-rule-hint">
              <InfoCircleOutlined style="color: #1890ff; margin-right: 6px" />
              到达设定周期与时间后自动执行一次排班；错过（关机/睡眠）会在下次启动时自动补跑
            </div>

            <div class="form-grid">
              <div class="config-item">
                <div class="config-label">触发模式</div>
                <Select
                  v-model:value="autoRunMode"
                  :options="AUTO_RUN_MODE_OPTIONS"
                  class="config-control"
                />
              </div>

              <div v-if="autoRunMode === 'Weekly'" class="config-item">
                <div class="config-label">每周几执行</div>
                <Select v-model:value="weeklyDay" :options="WEEKDAY_OPTIONS" class="config-control" />
              </div>

              <div v-if="autoRunMode === 'Monthly'" class="config-item">
                <div class="config-label">每月几号执行</div>
                <Select v-model:value="monthDay" :options="MONTH_DAY_OPTIONS" class="config-control" />
              </div>

              <div v-if="autoRunMode === 'Custom'" class="config-item">
                <div class="config-label">间隔天数</div>
                <div class="config-desc">距上次排班达到该天数后执行</div>
                <InputNumber v-model:value="customInterval" :min="1" :max="365" class="config-control" />
              </div>

              <div v-if="autoRunMode !== 'Off'" class="config-item">
                <div class="config-label">执行时间</div>
                <div class="config-desc">24 小时制 HH:MM，到点后 1 分钟内触发</div>
                <Input v-model:value="autoRunTime" placeholder="08:00" class="config-control" style="max-width: 120px" />
              </div>

              <div v-if="autoRunMode !== 'Off'" class="config-item">
                <div class="config-label">失败重试次数</div>
                <div class="config-desc">当天连续失败达到次数后放弃，次日重新计数</div>
                <InputNumber v-model:value="autoRunRetry" :min="0" :max="20" class="config-control" />
              </div>
            </div>
          </a-card>
        </div>
      </a-tab-pane>

      <!-- ======== AI 工具 ======== -->
      <a-tab-pane key="ai-tools" tab="AI 工具">
        <AiToolsPanel />
      </a-tab-pane>

      <!-- ======== 通知设置 ======== -->
      <a-tab-pane key="notification" tab="通知设置">
        <a-card class="mb-16">
          <template #title>通知入口</template>
          <template #extra>
            <Space>
              <Button size="small" @click="sendTestNotification">
                测试通知
              </Button>
              <Button
                type="primary"
                size="small"
                :loading="notificationSettingsLoading"
                @click="saveNotificationSettings"
              >
                <template #icon><SaveOutlined /></template>
                保存
              </Button>
            </Space>
          </template>

          <div class="config-grid">
            <div class="config-item">
              <div class="config-label">通知入口</div>
              <div class="config-desc">选择 Duty-Agent 通知投递到哪里</div>
              <Select
                v-model:value="notificationEntry"
                :options="notificationEntryOptions"
                style="width: 180px; margin-top: 6px"
                size="small"
              />
            </div>

            <div class="config-item">
              <div class="config-label">系统通知</div>
              <div class="config-desc">由独立客户端发送 Windows 通知</div>
              <Switch
                v-model:checked="systemNotificationsEnabled"
                :disabled="notificationEntry === 'classisland' || notificationEntry === 'off'"
                style="margin-top: 6px"
              />
            </div>
          </div>
        </a-card>

        <a-card>
          <template #title>通知内容</template>
          <div class="auto-run-grid">
            <div class="config-item">
              <div class="config-label">排班完成通知</div>
              <div class="config-desc">AI 排班成功或失败后发送通知</div>
              <Switch v-model:checked="scheduleCompletionNotificationEnabled" style="margin-top: 6px" />
            </div>

            <div class="config-item">
              <div class="config-label">自动排班启动通知</div>
              <div class="config-desc">自动排班任务触发时发送通知</div>
              <Switch v-model:checked="triggerNotification" style="margin-top: 6px" />
            </div>

            <div class="config-item">
              <div class="config-label">定时值日提醒</div>
              <div class="config-desc">按设定时间提醒今天的值日安排</div>
              <Switch v-model:checked="reminderEnabled" style="margin-top: 6px" />
            </div>

            <div class="config-item full-row">
              <div class="config-label">提醒时间</div>
              <div class="config-desc">多个时间用逗号分隔，例如 07:40, 12:10, 16:30</div>
              <Input
                v-model:value="reminderTimes"
                placeholder="07:40, 12:10"
                style="max-width: 360px; margin-top: 6px"
                size="small"
              />
            </div>

            <div class="config-item full-row">
              <div class="config-label">显示时长</div>
              <div class="config-desc">控制通知在屏幕上的停留时间</div>
              <div class="slider-row">
                <Slider
                  v-model:value="notificationDuration"
                  :min="3"
                  :max="15"
                  :marks="{ 3: '3 秒', 8: '8 秒', 15: '15 秒' }"
                  class="duration-slider"
                />
                <Tag color="blue" size="small">{{ notificationDuration }} 秒</Tag>
              </div>
            </div>
          </div>
        </a-card>
      </a-tab-pane>

      <!-- ======== 系统与自启 ======== -->
      <a-tab-pane key="system" tab="系统与自启">
        <div class="tab-content">
          <a-card>
            <template #title>
              <div class="card-title-row"><span>🖥️ 后台驻留与开机自启</span></div>
            </template>
            <template #extra>
              <Button
                type="primary"
                size="small"
                :loading="systemSettingsSaving"
                @click="saveSystemSettings"
              >
                <template #icon><SaveOutlined /></template>
                保存
              </Button>
            </template>

            <div class="duty-rule-hint">
              <InfoCircleOutlined style="color: #1890ff; margin-right: 6px" />
              驻留后台时后端持续运行，自动排班、值日提醒、错过补跑与离线兜底才能长期生效；本页开关仅对独立客户端生效（ClassIsland 插件模式由宿主管理生命周期）
            </div>

            <div class="form-grid">
              <div class="config-item">
                <div class="config-label">开机自启</div>
                <div class="config-desc">登录 Windows 后静默启动并驻留托盘（无需管理员权限）</div>
                <div style="margin-top: 6px"><Switch v-model:checked="clientAutoStart" /></div>
              </div>

              <div class="config-item">
                <div class="config-label">关闭主窗口时</div>
                <div class="config-desc">选“驻留后台”后点 X 不再退出程序，仅隐藏到托盘</div>
                <Select
                  v-model:value="clientCloseAction"
                  :options="CLOSE_ACTION_OPTIONS"
                  class="config-control"
                />
              </div>
            </div>
          </a-card>
        </div>
      </a-tab-pane>

    </a-tabs>
  </div>
</template>

<style scoped>
.tab-content {
  display: flex;
  flex-direction: column;
  gap: 0;
  min-width: 0;
}

:deep(.ant-tabs-content),
:deep(.ant-tabs-tabpane) {
  min-width: 0;
}

.mb-16 {
  margin-bottom: 16px;
}

/* Connection status */
.connection-status-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.connection-status-list {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.connection-status-item {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.status-hint {
  font-size: 13px;
  color: var(--da-text-secondary);
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

/* Config grid (vertical stack) */
.config-grid {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

/* Form grid (responsive two-column) */
.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 16px;
}

.form-grid > * {
  min-width: 0;
}

.full-row {
  grid-column: 1 / -1;
}

.config-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.config-item .config-control {
  margin-top: 6px;
  max-width: 360px;
  width: 100%;
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

.status-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 6px;
}

/* Auto run grid (notification content) */
.auto-run-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 20px;
}

.auto-run-grid > * {
  min-width: 0;
}

/* Slider row: flexible width instead of fixed 200px, so narrow WebView
   embeds don't overflow. */
.slider-row {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 6px;
  min-width: 0;
}

.duration-slider {
  flex: 1;
  min-width: 120px;
  max-width: 240px;
}

@media (max-width: 720px) {
  .auto-run-grid {
    grid-template-columns: 1fr;
  }
}
</style>
