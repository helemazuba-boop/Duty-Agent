/**
 * useScheduleChat — AI 排班对话的模块级单例。
 * 排班一次几十秒,用户很自然会切去别的页面再回来:
 * 对话流、run card、方案数据放在模块作用域,切路由不丢。
 * 页面只消费,不持有状态。
 */
import { ref } from 'vue';
import { message as antdMessage } from 'ant-design-vue';
import { api, getToken } from '@/api/http';
import { queryClient } from '@/queries/client';
import { snapshotKey } from '@/queries/keys';
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
  /** 后端确定性修正/提示(剔除缺席、缺额补齐、忽略幻觉账本、未覆盖日期...) */
  warnings?: string[];
  /** 排班来源: 'llm' | 'fallback'(代码兜底仍算成功,但用户应知道) */
  completionSource?: string;
  /** 模型思考过程(CoT)。推理模型经 reasoning_content 或 <think> 标签产出,
   *  后端作为旁路捕获后随 schedule_complete 下发;运行中由 stream_reasoning
   *  进度实时追加。代码兜底没有思考,为 undefined。 */
  reasoning?: string;
  /** 运行中是否已收到过 reasoning 增量(用于区分"没有思考"与"还没到") */
  sawReasoning?: boolean;
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

/**
 * 正计时心跳。
 *
 * `runDuration()` 在 running 期间用 `Date.now()`,而 Date.now() 不是响应式的——
 * 没有这个 ticker,卡片标题里的秒数会一直停在进入 running 那一刻的值(表现就是
 * "正计时不动")。排班一次几十秒,只在有 active run 时跑,run 一结束就停。
 *
 * 挂在模块作用域而不是页面组件上:useScheduleChat 是单例,用户切去别的页面再
 * 回来时对话流与 run card 都还在,挂在组件上的定时器会随路由卸载而停。
 */
const nowTick = ref(Date.now());
let ticker: number | undefined;

function startTicker() {
  if (ticker !== undefined) return;
  ticker = window.setInterval(() => {
    nowTick.value = Date.now();
    if (!activeRun.value) stopTicker();
  }, 1000);
}

function stopTicker() {
  if (ticker === undefined) return;
  window.clearInterval(ticker);
  ticker = undefined;
}

let lastInstruction = '';

function genId() {
  return `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * 从排班结果里取后端确定性修正/提示。
 * single_pass 用 adjustment_warnings；multi_agent 藏在 agent_summary.warnings。
 */
export function warningsOf(result: { [key: string]: unknown }): string[] {
  const out: string[] = [];
  const direct = result.adjustment_warnings;
  if (Array.isArray(direct)) {
    out.push(...direct.map((item) => String(item)).filter((text) => text.trim().length > 0));
  }
  const summary = result.agent_summary;
  if (summary && typeof summary === 'object') {
    const nested = (summary as { warnings?: unknown }).warnings;
    if (Array.isArray(nested)) {
      out.push(...nested.map((item) => String(item)).filter((text) => text.trim().length > 0));
    }
  }
  return out;
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

/**
 * 模型思考增量。
 *
 * 后端把 reasoning 走 `stream_reasoning` phase 的 stream_chunk 下发,与
 * `stream_chunk`(模型正文)同级但**绝不混流**——后端保证 reasoning 不进
 * content,这里也必须分开累积,否则思考会和排班 INI 搅在一起。
 *
 * 流式增量是"自上次回调以来的新增",所以直接拼接;结束时
 * schedule_complete 会带完整的 reasoning_content 覆盖它(某些 provider 的
 * 增量在末段被截断,完整版更可靠)。
 */
function appendReasoning(chunk: string) {
  const run = activeRun.value;
  if (!run || !chunk) return;
  run.sawReasoning = true;
  run.reasoning = (run.reasoning ?? '') + chunk;
}

/** 排班内容签名:iso → 去重人名(供 diff) */
export function scheduleSignature(entries: ScheduleEntry[]): Map<string, string> {
  const map = new Map<string, string>();
  for (const entry of entries) map.set(entry.date, personsOf(entry).join('|'));
  return map;
}

export async function fetchPlan() {
  try {
    workspace.value = await api.getSnapshot({ silent: true });
    planUpdatedAt.value = Date.now();
    await queryClient.invalidateQueries({ queryKey: snapshotKey });
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
    startTicker();
    messages.value.push({ id: genId(), role: 'assistant', content: '', ts: Date.now(), run });

    const result = await runSchedule({
      instruction: text,
      baseUrl: BASE_URL,
      token,
      onProgress: (p) => {
        // 思考与正文是两条独立的流:phase 决定去向,绝不合并。
        if (p.phase === 'stream_reasoning') {
          appendReasoning(p.stream_chunk ?? '');
          return;
        }
        pushProgress(p.phase || '执行', p.message);
      },
    });

    run.status = result.status === 'success' ? 'success' : 'error';
    run.endedAt = Date.now();
    activeRun.value = null;
    stopTicker();

    if (result.status === 'success') {
      run.result = result.ai_response || result.message || '排班已完成';
      // 完整版思考覆盖流式累积:某些 provider 的末段增量会丢,后端下发的
      // reasoning_content 是权威全文。没有思考(非推理模型/代码兜底)则不设。
      const reasoning = typeof result.reasoning_content === 'string'
        ? result.reasoning_content.trim()
        : '';
      if (reasoning) run.reasoning = reasoning;
      run.warnings = warningsOf(result);
      run.completionSource = typeof result.completion_source === 'string' ? result.completion_source : undefined;
      if (run.completionSource === 'fallback') {
        antdMessage.warning('排班完成（模型不可用，已由确定性算法兜底生成）');
      } else {
        antdMessage.success('排班完成');
      }
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
    stopTicker();
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
    nowTick,
    send,
    retry,
    clear,
    copyError,
  };
}
