/**
 * useScheduleWebSocket — WebSocket-based scheduling with SSE fallback.
 * Mirrors the C# RunScheduleViaSocketAsync → RunScheduleViaHttpAsync fallback pattern.
 */

import { ref } from 'vue';
import { apiUrl, wsUrl } from '@/api/baseUrl';
import { getToken } from '@/api/http';
import { splitSSEChunk, parseEventName, parseEventData, safeParse } from '@/utils/sseParser';

export interface ScheduleProgress {
  phase: string;
  message: string;
  stream_chunk?: string | null;
}

export interface ScheduleResult {
  status: string;
  message?: string;
  ai_response?: string;
  [key: string]: unknown;
}

export interface RunScheduleOptions {
  instruction: string;
  baseUrl: string;
  /** 可选：缺省时从 http.ts 的共享 getToken() 取（host 注入 token 的单源）。 */
  token?: string;
  onProgress?: (p: ScheduleProgress) => void;
  signal?: AbortSignal;
}

/** 运行期空闲超时：90s 内未收到任何 WS 消息即按错误收尾（区别于 5s 连接超时）。 */
const IDLE_TIMEOUT_MS = 90_000;

function getScheduleApiUrl(baseUrl: string): string {
  return baseUrl ? `${baseUrl}/api/v1/duty/schedule` : apiUrl('/api/v1/duty/schedule');
}

function getScheduleWsUrl(baseUrl: string, token?: string): string {
  if (baseUrl) {
    const url = baseUrl.replace(/^http/, 'ws') + '/api/v1/duty/live';
    if (token) {
      return `${url}?token=${encodeURIComponent(token)}`;
    }
    return url;
  }

  return wsUrl('/api/v1/duty/live');
}

async function runScheduleSSE(
  opts: Omit<RunScheduleOptions, 'onProgress'>,
  onProgress?: (p: ScheduleProgress) => void,
): Promise<ScheduleResult> {
  const { baseUrl, token, instruction, signal } = opts;
  const authToken = token || getToken() || '';

  const response = await fetch(getScheduleApiUrl(baseUrl), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${authToken}`,
    },
    body: JSON.stringify({ instruction }),
    signal,
  });

  if (!response.ok) {
    throw new Error(`SSE request failed: ${response.status} ${response.statusText}`);
  }

  const reader = response.body!.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let currentEvent = '';
  let dataBuffer = '';
  let finalResult: ScheduleResult | null = null;

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const { lines, rest } = splitSSEChunk(buffer);
      buffer = rest;

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) {
          if (currentEvent === 'complete') {
            if (dataBuffer) {
              finalResult = safeParse<ScheduleResult>(dataBuffer) ?? finalResult;
            }
            return finalResult ?? { status: 'error', message: 'No result from SSE stream' };
          }
          if (currentEvent === 'message' || currentEvent === '') {
            if (dataBuffer) {
              const evt = safeParse<{ phase?: string; message?: string }>(dataBuffer);
              if (evt) onProgress?.({ phase: evt.phase ?? '', message: evt.message ?? '' });
            }
          }
          currentEvent = '';
          dataBuffer = '';
          continue;
        }

        const evtName = parseEventName(trimmed);
        if (evtName !== null) {
          currentEvent = evtName;
          continue;
        }

        const evtData = parseEventData(trimmed);
        if (evtData !== null) {
          dataBuffer = evtData;
          continue;
        }
      }
    }
  } finally {
    reader.releaseLock();
  }

  return finalResult ?? { status: 'error', message: 'SSE stream ended without completion event' };
}

export function useScheduleWebSocket() {
  const isRunning = ref(false);
  const currentPhase = ref('');
  const progress = ref('');
  const error = ref<string | null>(null);

  async function runSchedule(opts: RunScheduleOptions): Promise<ScheduleResult> {
    const { baseUrl, token, instruction, onProgress, signal } = opts;
    const authToken = token || getToken() || '';
    isRunning.value = true;
    currentPhase.value = '';
    progress.value = '';
    error.value = null;

    let abortController: AbortController;
    if (signal) {
      abortController = new AbortController();
      signal.addEventListener('abort', () => abortController.abort());
    } else {
      abortController = new AbortController();
    }

    try {
      const scheduleWsUrl = getScheduleWsUrl(baseUrl, authToken);
      const clientChangeId = crypto.randomUUID().replace(/-/g, '');
      const traceId = `fe-${Date.now()}`;

      let ws: WebSocket | null = null;
      let wsDone = false;
      let idleTimer: number | undefined;

      const clearIdleTimer = () => {
        if (idleTimer !== undefined) {
          window.clearTimeout(idleTimer);
          idleTimer = undefined;
        }
      };

      // timeout 回调发生在 Promise executor 作用域之外，用此引用 resolve。
      let resolveIdle: (message: string) => void = () => {};

      // 运行期空闲看门狗：连接建立后每收到一条消息就重置；
      // 90s 静默（服务器挂起/半开连接）则按错误收尾，isRunning 在 finally 复位。
      const armIdleTimer = () => {
        clearIdleTimer();
        idleTimer = window.setTimeout(() => {
          idleTimer = undefined;
          if (wsDone || !ws) return;
          wsDone = true;
          const timeoutMessage = `连接超时：${IDLE_TIMEOUT_MS / 1000} 秒未收到服务器消息，已中止本次排班`;
          try { ws.close(); } catch { /* ignore */ }
          currentPhase.value = 'timeout';
          progress.value = timeoutMessage;
          onProgress?.({ phase: 'timeout', message: timeoutMessage });
          resolveIdle(timeoutMessage);
        }, IDLE_TIMEOUT_MS);
      };

      const wsResult = await new Promise<ScheduleResult>((resolve, reject) => {
        resolveIdle = (message) => resolve({ status: 'error', message });

        ws = new WebSocket(scheduleWsUrl);

        ws.onopen = () => {
          ws!.send(JSON.stringify({
            type: 'hello',
            trace_id: traceId,
            request_source: 'web_ui',
          }));
          armIdleTimer();
        };

        ws.onmessage = (evt) => {
          armIdleTimer();
          try {
            const msg = JSON.parse(evt.data as string);

            switch ((msg.type || '').trim().toLowerCase()) {
              case 'hello': {
                ws!.send(JSON.stringify({
                  type: 'schedule_run',
                  client_change_id: clientChangeId,
                  trace_id: traceId,
                  request_source: 'web_ui',
                  instruction,
                }));
                break;
              }
              case 'accepted': {
                break;
              }
              case 'schedule_progress': {
                const phase = (msg.phase || '') as string;
                const msgText = (msg.message || '') as string;
                const chunk = msg.stream_chunk ?? null;
                currentPhase.value = phase;
                progress.value = msgText;
                onProgress?.({ phase, message: msgText, stream_chunk: chunk });
                break;
              }
              case 'schedule_complete': {
                const result: ScheduleResult = { status: msg.status || 'ok' };
                if (msg.ai_response) result.ai_response = msg.ai_response;
                if (msg.message) result.message = msg.message;
                if (msg.status === 'success') {
                  result.status = 'success';
                }
                wsDone = true;
                clearIdleTimer();
                resolve(result);
                break;
              }
              case 'schedule_cancelled': {
                wsDone = true;
                clearIdleTimer();
                resolve({ status: 'cancelled', message: '排班执行已取消' });
                break;
              }
              case 'error': {
                wsDone = true;
                clearIdleTimer();
                const errMsg = (msg.message || 'Unknown error') as string;
                resolve({ status: 'error', message: errMsg });
                break;
              }
              default: {
                break;
              }
            }
          } catch (e) {
            console.warn('[useScheduleWebSocket] WS 消息解析失败:', e, evt.data);
          }
        };

        ws.onerror = () => {
          if (!wsDone) {
            wsDone = true;
            clearIdleTimer();
            reject(new Error('WebSocket connection error'));
          }
        };

        ws.onclose = () => {
          if (!wsDone) {
            wsDone = true;
            clearIdleTimer();
            reject(new Error('WebSocket closed unexpectedly'));
          }
        };

        // 连接超时（5s）：与运行期 idle 超时独立，仅覆盖"连不上"阶段。
        setTimeout(() => {
          if (!wsDone && ws && ws.readyState !== WebSocket.OPEN) {
            try { ws.close(); } catch { /* ignore */ }
            reject(new Error('WS_TIMEOUT'));
          }
        }, 5000);
      });

      clearIdleTimer();
      return wsResult;
    } catch (wsErr: unknown) {
      const wsErrMsg = wsErr instanceof Error ? wsErr.message : String(wsErr);

      if (wsErrMsg === 'WS_TIMEOUT' || wsErrMsg.includes('WebSocket') || wsErrMsg.includes('connection')) {
        try {
          const sseResult = await runScheduleSSE(
            { baseUrl, token: authToken, instruction, signal: abortController.signal },
            (p) => {
              currentPhase.value = p.phase;
              progress.value = p.message;
              onProgress?.(p);
            },
          );
          return sseResult;
        } catch (sseErr: unknown) {
          const msg = sseErr instanceof Error ? sseErr.message : String(sseErr);
          error.value = msg;
          return { status: 'error', message: msg };
        }
      }

      error.value = wsErrMsg;
      return { status: 'error', message: wsErrMsg };
    } finally {
      isRunning.value = false;
      abortController.abort();
    }
  }

  return {
    isRunning,
    currentPhase,
    progress,
    error,
    runSchedule,
  };
}
