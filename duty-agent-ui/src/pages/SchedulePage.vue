<script setup lang="ts">
import { ref, nextTick, watch } from 'vue';
import { Drawer, Input } from 'ant-design-vue';
import {
  SendOutlined,
  ClearOutlined,
  CheckCircleFilled,
  CloseCircleFilled,
  LoadingOutlined,
  RobotOutlined,
  LayoutOutlined,
  CopyOutlined,
  RedoOutlined,
} from '@ant-design/icons-vue';
import PageHeader from '@/components/ui/PageHeader.vue';
import PlanPanel from '@/components/schedule/PlanPanel.vue';
import {
  useScheduleChat,
  type ChatMsg,
  type RunInfo,
} from '@/composables/useScheduleChat';

const instruction = ref('');
const messagesEl = ref<HTMLElement | null>(null);
const inputEl = ref<{ focus: (opts?: object) => void } | null>(null);
const planDrawerOpen = ref(false);
const expandedRuns = ref(new Set<string>());

const {
  messages,
  isRunning,
  currentPhase,
  progress,
  send,
  retry,
  clear,
  copyError,
} = useScheduleChat();

// ======== 快捷指令:点击填入输入框(可继续补充),Shift+点击 直发 ========
const quickInstructions = [
  { label: '排下周', prompt: '请帮我安排下周的值日表,按公平轮转' },
  { label: '补齐缺口', prompt: '请检查未来 7 天未安排值日的日期并补齐' },
  { label: '按公平分配', prompt: '请按值班次数均衡的原则重新分配本月值日' },
  { label: '谁值班最多?', prompt: '统计本月每人值班次数,列出值班最多和最少的人' },
];

function handleQuick(event: MouseEvent, prompt: string) {
  if (event.shiftKey) {
    send(prompt);
    return;
  }
  instruction.value = prompt;
  inputEl.value?.focus?.();
}

// ======== 发送 / 键盘 ========
async function handleRun() {
  const text = instruction.value.trim();
  if (!text || isRunning.value) return;
  instruction.value = '';
  await send(text);
}

function handleKeyDown(e: KeyboardEvent) {
  // IME 组合中(拼音选词确认)的回车不发送
  if (e.isComposing || e.keyCode === 229) return;
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    handleRun();
  }
}

function handleClear() {
  clear();
}

function scrollToBottom() {
  nextTick(() => {
    if (messagesEl.value) {
      messagesEl.value.scrollTop = messagesEl.value.scrollHeight;
    }
  });
}

watch(() => messages.value.length, scrollToBottom);
watch(() => messages.value[messages.value.length - 1]?.run?.groups.length, scrollToBottom);
watch(() => messages.value[messages.value.length - 1]?.content, scrollToBottom);

// ======== run card 渲染辅助 ========
interface RunRow {
  phase: string;
  text: string;
  count: number;
  ts: number;
}

function flattenRows(run: RunInfo): RunRow[] {
  const rows: RunRow[] = [];
  for (const group of run.groups) {
    for (const step of group.steps) {
      rows.push({ phase: group.phase, text: step.text, count: step.count, ts: step.ts });
    }
  }
  return rows;
}

function runDuration(run: RunInfo): string {
  if (!run.endedAt) return '';
  return `${((run.endedAt - run.startedAt) / 1000).toFixed(1)}s`;
}

function stepOffset(run: RunInfo, ts: number): string {
  return `+${((ts - run.startedAt) / 1000).toFixed(1)}s`;
}

function stepTotal(run: RunInfo): number {
  return run.groups.reduce((sum, g) => sum + g.steps.length, 0);
}

function isRunExpanded(msg: ChatMsg): boolean {
  return expandedRuns.value.has(msg.id);
}

function toggleRun(msg: ChatMsg) {
  const next = new Set(expandedRuns.value);
  if (next.has(msg.id)) next.delete(msg.id);
  else next.add(msg.id);
  expandedRuns.value = next;
}

/** running 时只渲染最后 N 行,天然不需要内部滚动 */
const RUNNING_VISIBLE = 4;

function rowsOf(msg: ChatMsg): RunRow[] {
  const run = msg.run!;
  const rows = flattenRows(run);
  if (run.status === 'running' && rows.length > RUNNING_VISIBLE && !expandedRuns.value.has(msg.id)) {
    return rows.slice(-RUNNING_VISIBLE);
  }
  return rows;
}

function hiddenCount(msg: ChatMsg): number {
  const run = msg.run!;
  const total = flattenRows(run).length;
  return total - rowsOf(msg).length;
}

// ======== 时间分隔:>5 分钟才显示,首条消息不显示 ========
const showTimeDivider = (index: number): boolean => {
  if (index === 0) return false;
  return messages.value[index]!.ts - messages.value[index - 1]!.ts > 5 * 60 * 1000;
};

const fmtTime = (ts: number) =>
  new Date(ts).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
</script>

<template>
  <div class="ai-page">
    <PageHeader title="AI 排班">
      <template #subtitle>
        {{ messages.length ? `${messages.length} 条消息` : '用自然语言安排值日,结果实时写回排班表' }}
      </template>
      <template #actions>
        <a-button class="ai-plan-drawer-trigger" size="small" @click="planDrawerOpen = true">
          <template #icon><LayoutOutlined /></template>
          方案
        </a-button>
        <a-button v-if="messages.length && !isRunning" size="small" @click="handleClear">
          <template #icon><ClearOutlined /></template>
          新对话
        </a-button>
      </template>
    </PageHeader>

    <div class="ai-layout">
      <!-- ======== 左栏:对话流 ======== -->
      <section class="ai-chat">
        <div ref="messagesEl" class="ai-chat__scroll">
          <div class="ai-chat__inner">
            <!-- 空状态 -->
            <div v-if="!messages.length" class="ai-chat__empty">
              <div class="ai-chat__empty-icon"><RobotOutlined /></div>
              <div class="ai-chat__empty-title">让 AI 排班</div>
              <p class="ai-chat__empty-desc">
                直接输入需求,或点击下方快捷指令;Shift + 点击快捷指令立即发送。
                执行过程和结果都会显示在这里,切到其他页面也不会中断。
              </p>
            </div>

            <template v-for="(msg, i) in messages" :key="msg.id">
              <div v-if="showTimeDivider(i)" class="ai-divider"><span>{{ fmtTime(msg.ts) }}</span></div>

              <!-- 用户消息 -->
              <div v-if="msg.role === 'user'" class="ai-msg ai-msg--user">
                <div class="ai-msg__bubble">{{ msg.content }}</div>
              </div>

              <!-- AI run card -->
              <div v-else class="ai-msg ai-msg--assistant">
                <div class="ai-msg__logo"><RobotOutlined /></div>
                <div class="ai-msg__body">
                  <div
                    class="ai-run"
                    :class="{
                      'ai-run--running': msg.run!.status === 'running',
                      'ai-run--done': msg.run!.status !== 'running',
                    }"
                  >
                    <button
                      type="button"
                      class="ai-run__head"
                      :aria-expanded="msg.run!.status !== 'running' ? isRunExpanded(msg) : undefined"
                      @click="msg.run!.status !== 'running' && toggleRun(msg)"
                    >
                      <component
                        :is="msg.run!.status === 'running' ? LoadingOutlined : msg.run!.status === 'success' ? CheckCircleFilled : CloseCircleFilled"
                        class="ai-run__icon"
                        :class="`ai-run__icon--${msg.run!.status}`"
                      />
                      <span class="ai-run__title">
                        <template v-if="msg.run!.status === 'running'">
                          {{ currentPhase || progress || '执行中' }}
                        </template>
                        <template v-else-if="msg.run!.status === 'success'">
                          排班完成 · {{ stepTotal(msg.run!) }} 步 · {{ runDuration(msg.run!) }}
                        </template>
                        <template v-else>执行失败</template>
                      </span>
                      <span v-if="msg.run!.status !== 'running'" class="ai-run__chevron" :class="{ 'ai-run__chevron--open': isRunExpanded(msg) }">▾</span>
                    </button>

                    <!-- 步骤(运行中默认只显示最后 4 行) -->
                    <div v-if="msg.run!.status === 'running' || isRunExpanded(msg)" class="ai-run__steps">
                      <button
                        v-if="hiddenCount(msg) > 0"
                        type="button"
                        class="ai-run__more"
                        @click.stop="toggleRun(msg)"
                      >
                        显示全部 {{ stepTotal(msg.run!) }} 步
                      </button>
                      <div v-for="(row, si) in rowsOf(msg)" :key="si" class="ai-run__step">
                        <span class="ai-run__step-dot" :class="{ 'ai-run__step-dot--last': msg.run!.status === 'running' && si === rowsOf(msg).length - 1 }" />
                        <span class="ai-run__step-text">
                          <span class="ai-run__step-phase">{{ row.phase }}</span>
                          <span :class="{ 'da-streaming-cursor': msg.run!.status === 'running' && si === rowsOf(msg).length - 1 }">{{ row.text }}</span>
                          <span v-if="row.count > 1" class="ai-run__step-count">×{{ row.count }}</span>
                        </span>
                        <span class="ai-run__step-time da-tnum">{{ stepOffset(msg.run!, row.ts) }}</span>
                      </div>
                    </div>

                    <!-- 结果摘要:渲染在同一张 card 正文区 -->
                    <div v-if="msg.run!.status === 'success' && msg.run!.result && isRunExpanded(msg)" class="ai-run__result">
                      {{ msg.run!.result }}
                    </div>

                    <!-- 失败:错误信息 + 重试 / 复制错误 -->
                    <div v-if="msg.run!.status === 'error'" class="ai-run__error">
                      <div class="ai-run__error-text">{{ msg.run!.errorText }}</div>
                      <div class="ai-run__error-actions">
                        <a-button size="small" @click="retry">
                          <template #icon><RedoOutlined /></template>
                          重试
                        </a-button>
                        <a-button size="small" @click="copyError(msg.run!)">
                          <template #icon><CopyOutlined /></template>
                          复制错误
                        </a-button>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </template>
          </div>
        </div>

        <!-- ======== 底部输入区 ======== -->
        <div class="ai-input">
          <div class="ai-input__chips">
            <button
              v-for="q in quickInstructions"
              :key="q.label"
              type="button"
              class="ai-input__chip"
              :disabled="isRunning"
              :title="q.prompt"
              @click="handleQuick($event, q.prompt)"
            >
              {{ q.label }}
            </button>
          </div>
          <div class="ai-input__box">
            <!-- 运行中不禁用输入框:用户可以提前打下一句 -->
            <Input.TextArea
              ref="inputEl"
              v-model:value="instruction"
              :auto-size="{ minRows: 1, maxRows: 6 }"
              placeholder="描述排班需求,Enter 发送,Shift+Enter 换行"
              @keydown="handleKeyDown"
            />
            <a-button
              type="primary"
              class="ai-input__send"
              :disabled="!instruction.trim() || isRunning"
              @click="handleRun"
            >
              <template #icon>
                <LoadingOutlined v-if="isRunning" />
                <SendOutlined v-else />
              </template>
            </a-button>
          </div>
        </div>
      </section>

      <!-- ======== 右栏:方案面板(窄屏进 Drawer) ======== -->
      <aside class="ai-plan">
        <PlanPanel />
      </aside>
    </div>

    <Drawer
      v-model:open="planDrawerOpen"
      title="当前方案"
      width="380"
      placement="right"
      class="ai-plan-drawer"
    >
      <PlanPanel />
    </Drawer>
  </div>
</template>

<style scoped>
.ai-page {
  display: flex;
  flex-direction: column;
  /* .app-content 的上下 padding(20 + 24)已由 shell 持有 */
  height: calc(100vh - 44px);
  min-height: 560px;
}

/* ======== 两栏 ======== */
.ai-layout {
  display: flex;
  flex: 1;
  gap: 16px;
  min-height: 0;
}

.ai-chat {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--dt-surface);
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius-lg);
  overflow: hidden;
}

.ai-chat__scroll {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
}

.ai-chat__inner {
  max-width: 720px;
  margin: 0 auto;
  padding: 20px 24px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

/* ---- 空状态 ---- */
.ai-chat__empty {
  margin: auto;
  text-align: center;
  padding: 48px 24px;
}

.ai-chat__empty-icon {
  font-size: 32px;
  color: var(--dt-text-3);
  margin-bottom: 8px;
}

.ai-chat__empty-title {
  font-size: 16px;
  font-weight: 600;
  color: var(--dt-text);
}

.ai-chat__empty-desc {
  margin: 6px auto 0;
  max-width: 400px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--dt-text-2);
}

/* ---- 时间分隔 ---- */
.ai-divider {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--dt-text-3);
  font-size: 11px;
}

.ai-divider::before,
.ai-divider::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--dt-border);
}

/* ---- 消息 ---- */
.ai-msg {
  display: flex;
  gap: 10px;
  min-width: 0;
}

.ai-msg--user {
  justify-content: flex-end;
}

.ai-msg__bubble {
  max-width: 78%;
  padding: 8px 14px;
  border-radius: 12px;
  background: var(--dt-primary-soft);
  color: var(--dt-text);
  font-size: 14px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.ai-msg__logo {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 8px;
  background: var(--dt-surface-2);
  color: var(--dt-text-2);
  font-size: 13px;
  flex-shrink: 0;
}

.ai-msg__body {
  min-width: 0;
  flex: 1;
}

/* ---- Run card ---- */
.ai-run {
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius);
  overflow: hidden;
}

.ai-run--running {
  border-color: color-mix(in srgb, var(--dt-live) 45%, transparent);
}

.ai-run__head {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  background: var(--dt-surface-2);
  border: 0;
  font-size: 13px;
  text-align: left;
  cursor: default;
}

.ai-run--done .ai-run__head {
  cursor: pointer;
}

.ai-run__icon--running { color: var(--dt-live); }
.ai-run__icon--success { color: var(--dt-success); }
.ai-run__icon--error { color: var(--dt-danger); }

.ai-run__title {
  font-weight: 600;
  color: var(--dt-text);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ai-run__chevron {
  margin-left: auto;
  color: var(--dt-text-3);
  font-size: 11px;
  transition: transform 0.15s ease;
}

.ai-run__chevron--open {
  transform: rotate(180deg);
}

.ai-run__steps {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
}

.ai-run__more {
  align-self: flex-start;
  margin-bottom: 2px;
  padding: 0;
  border: 0;
  background: none;
  font-size: 11px;
  color: var(--dt-live);
  cursor: pointer;
}

.ai-run__step {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 12px;
  line-height: 1.5;
  color: var(--dt-text-2);
}

.ai-run__step-dot {
  width: 5px;
  height: 5px;
  border-radius: 999px;
  background: var(--dt-border-strong);
  flex-shrink: 0;
  transform: translateY(-2px);
}

.ai-run__step-dot--last {
  background: var(--dt-live);
  animation: daPulse 1.2s ease-out infinite;
}

.ai-run__step-text {
  min-width: 0;
  flex: 1;
  word-break: break-word;
}

.ai-run__step-phase {
  font-weight: 600;
  color: var(--dt-text-2);
  margin-right: 6px;
}

.ai-run__step-count {
  color: var(--dt-text-3);
  margin-left: 4px;
}

.ai-run__step-time {
  font-size: 11px;
  color: var(--dt-text-3);
  flex-shrink: 0;
}

.ai-run__result {
  padding: 10px 12px;
  border-top: 1px solid var(--dt-border-2);
  font-size: 14px;
  line-height: 1.7;
  color: var(--dt-text);
  white-space: pre-wrap;
  word-break: break-word;
}

.ai-run__error {
  padding: 10px 12px;
  border-top: 1px solid color-mix(in srgb, var(--dt-danger) 35%, transparent);
}

.ai-run__error-text {
  font-size: 13px;
  color: var(--dt-danger);
  margin-bottom: 8px;
  word-break: break-word;
}

.ai-run__error-actions {
  display: flex;
  gap: 8px;
}

/* ======== 底部输入区 ======== */
.ai-input {
  border-top: 1px solid var(--dt-border);
  background: var(--dt-surface);
  padding: 10px 16px 12px;
}

.ai-input__chips {
  max-width: 720px;
  margin: 0 auto 8px;
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.ai-input__chip {
  padding: 2px 10px;
  border: 1px solid var(--dt-border);
  border-radius: 999px;
  background: transparent;
  color: var(--dt-text-2);
  font-size: 12px;
  cursor: pointer;
  transition: color 0.15s ease, border-color 0.15s ease, background 0.15s ease;
}

.ai-input__chip:hover:not(:disabled) {
  color: var(--dt-primary);
  border-color: var(--dt-primary);
  background: var(--dt-primary-soft);
}

.ai-input__chip:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.ai-input__box {
  max-width: 720px;
  margin: 0 auto;
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

.ai-input__box :deep(.ant-input) {
  border-radius: var(--dt-radius);
  resize: none;
}

.ai-input__send {
  flex-shrink: 0;
}

/* ======== 右栏 ======== */
.ai-plan {
  width: 380px;
  flex-shrink: 0;
  overflow-y: auto;
  min-height: 0;
}

/* 窄窗口:右栏进 Drawer,由 PageHeader 的"方案"按钮打开 */
@media (max-width: 1280px) {
  .ai-plan {
    display: none;
  }
}

@media (min-width: 1281px) {
  .ai-plan-drawer-trigger {
    display: none;
  }
}
</style>
