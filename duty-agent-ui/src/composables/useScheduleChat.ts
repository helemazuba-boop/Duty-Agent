/**
 * useScheduleChat — AI 排班对话的模块级单例。
 * 排班一次几十秒,用户很自然会切去别的页面再回来:
 * 对话流、run card、方案数据放在模块作用域,切路由不丢。
 * 页面只消费,不持有状态。
 */
import { ref } from 'vue';
import { message as antdMessage } from 'ant-design-vue';
import { api, getToken } from '@/api/http';
import { useScheduleWebSocket } from './useScheduleWebSocket';
import { personsOf } from '@/utils/date';
import type { ScheduleEntry, Workspace } from '@/types';

export interface RunStep {
  text: string;
  ts: number;
  count: number;
}

export interface RunGroup {
  phase: string;
  steps: RunStep[];
}

export interface RunInfo {
  status: 'running' | 'success' | 'error';
  startedAt: number;
  endedAt?: number;
  groups: RunGroup[];
  /** 成功后的 ai_response 摘要,渲染在同一张 run card 正文里 */
  result?: string;
  errorText?: string;
}

export interface ChatMsg {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  ts: number;
  run?: RunInfo;
}

// baseUrl 为空 → 走 Vite 代理(dev)或 /app/*(desktop)
const BASE_URL = '';

const messages = ref<ChatMsg[]>([]);
const activeRun = ref<RunInfo | null>(null);
const workspace = ref<Workspace | null>(null);
const planUpdatedAt = ref(0);
/** 发送前的排班签名(iso → 人名),供方案面板 diff "本次改了哪几天" */
const planBefore = ref<Map<string, string>>(new Map());

const { isRunning, currentPhase, progress, runSchedule } = useScheduleWebSocket();

let lastInstruction = '';

function genId() {
  return `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

/** 进度按 phase 分组;同组连续相同文本折叠为一条 + 计数 */
function pushProgress(phase: string, text: string) {
  const run = activeRun.value;
  if (!run) return;
  const lastGroup = run.groups[run.groups.length - 1];
  if (lastGroup && lastGroup.phase === phase) {
    const last = lastGroup.steps[lastGroup.steps.length - 1];
    if (last && last.text === text) {
      last.count += 1;
      return;
    }
    lastGroup.steps.push({ text, ts: Date.now(), count: 1 });
    return;
  }
  run.groups.push({ phase: phase || '执行', steps: [{ text, ts: Date.now(), count: 1 }] });
}

/** 排班内容签名:iso → 去重人名(供 diff) */
export function scheduleSignature(entries: ScheduleEntry[]): Map<string, string> {
  const map = new Map<string, string>();
  for (const entry of entries) map.set(entry.date, personsOf(entry).join('|'));
  return map;
}

export async function fetchPlan() {
  try {
    workspace.value = await api.getSnapshot();
    planUpdatedAt.value = Date.now();
  } catch {
    /* 方案面板刷新失败不影响对话 */
  }
}

export function useScheduleChat() {
  async function send(instruction: string): Promise<boolean> {
    const text = instruction.trim();
    if (!text || isRunning.value) return false;

    const token = getToken();
    if (!token) {
      antdMessage.warning('未检测到访问令牌,请刷新页面后重试');
      return false;
    }

    lastInstruction = text;
    messages.value.push({ id: genId(), role: 'user', content: text, ts: Date.now() });

    planBefore.value = scheduleSignature(workspace.value?.state?.schedule_pool ?? []);
    const run: RunInfo = { status: 'running', startedAt: Date.now(), groups: [] };
    activeRun.value = run;
    messages.value.push({ id: genId(), role: 'assistant', content: '', ts: Date.now(), run });

    const result = await runSchedule({
      instruction: text,
      baseUrl: BASE_URL,
      token,
      onProgress: (p) => pushProgress(p.phase || '执行', p.message),
    });

    run.status = result.status === 'success' ? 'success' : 'error';
    run.endedAt = Date.now();
    activeRun.value = null;

    if (result.status === 'success') {
      run.result = result.ai_response || result.message || '排班已完成';
      antdMessage.success('排班完成');
      await fetchPlan();
    } else {
      run.errorText = result.message || '未知错误';
      antdMessage.error('排班执行失败');
    }
    return result.status === 'success';
  }

  async function retry(): Promise<boolean> {
    if (!lastInstruction || isRunning.value) return false;
    return send(lastInstruction);
  }

  function clear() {
    messages.value = [];
    activeRun.value = null;
  }

  async function copyError(run: RunInfo) {
    try {
      await navigator.clipboard.writeText(run.errorText || '');
      antdMessage.success('错误信息已复制');
    } catch {
      antdMessage.warning('复制失败,请手动复制');
    }
  }

  return {
    messages,
    isRunning,
    currentPhase,
    progress,
    activeRun,
    workspace,
    planUpdatedAt,
    planBefore,
    send,
    retry,
    clear,
    copyError,
  };
}
