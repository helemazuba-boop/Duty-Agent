<script setup lang="ts">
import { ref, computed, nextTick, watch } from 'vue';
import { Input, Button, Space, Tag, message } from 'ant-design-vue';
import { ThunderboltOutlined, ClearOutlined } from '@ant-design/icons-vue';
import { useScheduleWebSocket } from '@/composables/useScheduleWebSocket';
import { getToken } from '@/api/http';

const instruction = ref('');
const messagesEl = ref<HTMLElement | null>(null);

// Page title reflects whether the panel has history
const hasHistory = ref(false);

interface ChatMsg {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  phase?: string;
  timestamp: number;
}

const messages = ref<ChatMsg[]>([]);

const { isRunning, currentPhase, progress, runSchedule } = useScheduleWebSocket();

// baseUrl is empty → requests go through Vite proxy (dev) or match /app/* (desktop)
const BASE_URL = '';

// Quick commands
const quickInstructions = [
  '请帮我安排本周的值日生',
  '安排张三和李四在A区域',
  '查看本周排班情况',
];

// Build current phase tag label
const phaseLabel = computed(() => {
  if (!isRunning.value) return '';
  return currentPhase.value || progress.value || '执行中...';
});

function genId() {
  return `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

function addUserMessage(text: string) {
  messages.value.push({
    id: genId(),
    role: 'user',
    content: text,
    timestamp: Date.now(),
  });
  hasHistory.value = true;
}

function addSystemMessage(text: string, phase?: string) {
  messages.value.push({
    id: genId(),
    role: 'system',
    content: text,
    phase,
    timestamp: Date.now(),
  });
}

function updateLastSystemMessage(text: string) {
  const last = messages.value[messages.value.length - 1];
  if (last && last.role === 'system') {
    last.content = text;
  }
}

async function handleRun() {
  const text = instruction.value.trim();
  if (!text || isRunning.value) return;

  const token = getToken();
  if (!token) {
    message.warning('未检测到访问令牌，请刷新页面后重试');
    return;
  }

  instruction.value = '';
  addUserMessage(text);

  const result = await runSchedule({
    instruction: text,
    baseUrl: BASE_URL,
    token,
    onProgress: (p) => {
      updateLastSystemMessage(`${p.phase ? p.phase + '：' : ''}${p.message}`);
    },
  });

  if (result.status === 'success') {
    const aiMsg = result.ai_response || result.message || '排班已完成';
    addSystemMessage(aiMsg);
    message.success('排班完成');
  } else {
    addSystemMessage(`执行失败：${result.message || '未知错误'}`, 'error');
    message.error('排班执行失败');
  }
}

async function handleQuickCommand(text: string) {
  instruction.value = text;
  await handleRun();
}

function handleClear() {
  messages.value = [];
  hasHistory.value = false;
}

function handleKeyDown(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    handleRun();
  }
}

function scrollToBottom() {
  nextTick(() => {
    if (messagesEl.value) {
      messagesEl.value.scrollTop = messagesEl.value.scrollHeight;
    }
  });
}

watch(() => messages.value.length, () => {
  scrollToBottom();
});

watch(isRunning, (running) => {
  if (running) {
    addSystemMessage('开始执行...');
  }
});
</script>

<template>
  <div class="page-container animate-fade-in">
    <!-- Page header -->
    <div class="page-header">
      <div>
        <h1 class="page-title">AI 排班</h1>
        <p class="page-subtitle">
          <template v-if="hasHistory">
            {{ messages.length }} 条消息
          </template>
          <template v-else>
            通过自然语言指令管理排班
          </template>
        </p>
      </div>
      <Space>
        <Tag v-if="isRunning" color="processing" style="border-radius: 12px">
          {{ phaseLabel }}
        </Tag>
        <a-button
          v-if="hasHistory && !isRunning"
          size="small"
          @click="handleClear"
        >
          <template #icon><ClearOutlined /></template>
          清空记录
        </a-button>
      </Space>
    </div>

    <!-- Main layout: left (form) + right (chat history) -->
    <div class="schedule-main-layout">
      <!-- ======== LEFT: Instruction Form ======== -->
      <a-card
        :bordered="false"
        class="schedule-form-card animate-fade-in-up"
        style="animation-delay: 0.05s"
      >
        <template #title>
          <div style="display: flex; align-items: center; gap: 8px">
            <span style="font-size: 18px">🤖</span>
            <span style="font-weight: 600">发送排班指令</span>
          </div>
        </template>

        <div class="form-label">排班指令（可选）</div>
        <Input.TextArea
          v-model:value="instruction"
          :rows="4"
          :maxlength="500"
          show-count
          placeholder="描述你的排班需求，例如：本周优先安排张三和李四在A区域，周三王五休息..."
          :disabled="isRunning"
          @keydown="handleKeyDown"
          style="margin-bottom: 12px"
        />
        <div class="form-hint">
          不填指令时，AI 将根据花名册和历史数据自动生成均衡的排班方案
        </div>

        <Button
          type="primary"
          size="large"
          block
          :loading="isRunning"
          :disabled="!instruction.trim()"
          @click="handleRun"
          class="run-btn"
          style="margin-top: 12px"
        >
          <template #icon><ThunderboltOutlined /></template>
          {{ isRunning ? '执行中...' : '执行 AI 排班' }}
        </Button>

        <!-- Quick commands -->
        <div class="quick-commands-section">
          <div class="quick-commands-title">快捷指令</div>
          <div class="quick-commands-list">
            <div
              v-for="q in quickInstructions"
              :key="q"
              class="hint-item"
              :class="{ disabled: isRunning }"
              @click="!isRunning && handleQuickCommand(q)"
            >
              {{ q }}
            </div>
          </div>
          <div class="hints-tip">点击快捷指令直接执行</div>
        </div>
      </a-card>

      <!-- ======== RIGHT: Chat History ======== -->
      <a-card
        :bordered="false"
        class="schedule-chat-card animate-fade-in-up"
        style="animation-delay: 0.1s"
      >
        <template #title>
          <div style="display: flex; align-items: center; gap: 8px">
            <span style="font-size: 18px">💬</span>
            <span style="font-weight: 600">对话历史</span>
            <Tag v-if="isRunning" color="processing" size="small" style="border-radius: 10px; margin-left: 4px">
              {{ phaseLabel }}
            </Tag>
          </div>
        </template>

        <!-- Empty state -->
        <div v-if="messages.length === 0" class="chat-empty">
          <div class="chat-empty-icon">👋</div>
          <div class="chat-empty-title">开始对话</div>
          <div class="chat-empty-desc">在左侧输入排班指令或点击快捷指令</div>
        </div>

        <!-- Messages list -->
        <div ref="messagesEl" class="chat-messages">
          <template v-for="msg in messages" :key="msg.id">
            <!-- User message -->
            <div v-if="msg.role === 'user'" class="msg-row msg-user-row">
              <div class="msg-bubble msg-user-bubble">
                <div class="msg-content">{{ msg.content }}</div>
                <div class="msg-time">{{ new Date(msg.timestamp).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' }) }}</div>
              </div>
            </div>

            <!-- System/assistant message -->
            <div v-else class="msg-row msg-assistant-row">
              <div
                class="msg-bubble msg-assistant-bubble"
                :class="{ 'msg-running': isRunning && msg === messages[messages.length - 1] && msg.role === 'system' }"
              >
                <div class="msg-content">{{ msg.content }}</div>
                <div class="msg-time">{{ new Date(msg.timestamp).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' }) }}</div>
              </div>
            </div>
          </template>
        </div>
      </a-card>
    </div>

    <!-- Info card -->
    <a-card
      :bordered="false"
      class="animate-fade-in-up"
      style="animation-delay: 0.15s"
    >
      <template #title>使用说明</template>
      <div class="info-list">
        <div class="info-item">
          <div class="info-num">1</div>
          <div class="info-content">
            <div class="info-title">配置花名册</div>
            <div class="info-desc">在「花名册管理」中添加值日人员</div>
          </div>
        </div>
        <div class="info-item">
          <div class="info-num">2</div>
          <div class="info-content">
            <div class="info-title">发送排班指令</div>
            <div class="info-desc">描述你的排班需求或使用快捷指令</div>
          </div>
        </div>
        <div class="info-item">
          <div class="info-num">3</div>
          <div class="info-content">
            <div class="info-title">查看结果</div>
            <div class="info-desc">排班结果自动保存，前往「排班安排」查看</div>
          </div>
        </div>
      </div>
    </a-card>
  </div>
</template>

<style scoped>
/* ======== Main layout ======== */
.schedule-main-layout {
  display: grid;
  grid-template-columns: 420px 1fr;
  gap: 16px;
  align-items: start;
}

@media (max-width: 900px) {
  .schedule-main-layout {
    grid-template-columns: 1fr;
  }
}

/* ======== Form card ======== */
.schedule-form-card {
  height: fit-content;
}

.form-label {
  font-weight: 500;
  font-size: 14px;
  color: var(--da-text-primary);
  margin-bottom: 8px;
}

.form-hint {
  font-size: 12px;
  color: var(--da-text-secondary);
  background: #fafafa;
  padding: 8px 12px;
  border-radius: 6px;
  border-left: 3px solid #1890ff;
}

.run-btn {
  height: 44px;
  font-size: 15px;
  border-radius: 8px;
}

/* ======== Quick commands ======== */
.quick-commands-section {
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid var(--da-border);
}

.quick-commands-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--da-text-secondary);
  margin-bottom: 10px;
}

.quick-commands-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.hint-item {
  font-size: 12px;
  color: var(--da-text-primary);
  background: #fff;
  border: 1px solid var(--da-border);
  border-radius: 6px;
  padding: 6px 10px;
  cursor: pointer;
  transition: all 0.2s;
  line-height: 1.4;
}

.hint-item:hover:not(.disabled) {
  border-color: #1890ff;
  background: #e6f7ff;
  color: #096dd9;
}

.hint-item.disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.hints-tip {
  font-size: 11px;
  color: var(--da-text-muted);
  margin-top: 8px;
  text-align: center;
}

/* ======== Chat card ======== */
.schedule-chat-card {
  height: calc(100vh - 220px);
  min-height: 400px;
  display: flex;
  flex-direction: column;
}

.schedule-chat-card :deep(.ant-card-body) {
  display: flex;
  flex-direction: column;
  height: calc(100% - 57px);
  padding: 0;
  overflow: hidden;
}

/* Chat empty state */
.chat-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  flex: 1;
  padding: 40px 20px;
  text-align: center;
  color: var(--da-text-secondary);
}

.chat-empty-icon {
  font-size: 48px;
  margin-bottom: 8px;
}

.chat-empty-title {
  font-size: 15px;
  font-weight: 500;
  color: var(--da-text-primary);
  margin-bottom: 4px;
}

.chat-empty-desc {
  font-size: 13px;
  color: var(--da-text-secondary);
}

/* Chat messages */
.chat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.chat-messages::-webkit-scrollbar {
  width: 6px;
}

.chat-messages::-webkit-scrollbar-thumb {
  background: #e8e8e8;
  border-radius: 3px;
}

.chat-messages::-webkit-scrollbar-thumb:hover {
  background: #d9d9d9;
}

/* Message rows */
.msg-row {
  display: flex;
  width: 100%;
}

.msg-user-row {
  justify-content: flex-end;
}

.msg-assistant-row {
  justify-content: flex-start;
}

.msg-bubble {
  max-width: 80%;
  padding: 10px 14px;
  border-radius: 8px;
  font-size: 14px;
  line-height: 1.6;
  word-break: break-word;
}

.msg-user-bubble {
  background: #1890ff;
  color: #fff;
  border-bottom-right-radius: 2px;
}

.msg-assistant-bubble {
  background: #fff;
  color: #262626;
  border: 1px solid #f0f0f0;
  border-bottom-left-radius: 2px;
}

.msg-running {
  border-color: #91d5ff;
  background: #f0f7ff;
}

.msg-content {
  white-space: pre-wrap;
}

.msg-time {
  font-size: 11px;
  opacity: 0.6;
  margin-top: 4px;
  text-align: right;
}

/* ======== Info list ======== */
.info-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.info-item {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.info-num {
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: #1890ff;
  color: #fff;
  font-size: 13px;
  font-weight: 600;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.info-content {
  flex: 1;
}

.info-title {
  font-weight: 600;
  font-size: 14px;
  color: var(--da-text-primary);
  margin-bottom: 2px;
}

.info-desc {
  font-size: 12px;
  color: var(--da-text-secondary);
}
</style>
