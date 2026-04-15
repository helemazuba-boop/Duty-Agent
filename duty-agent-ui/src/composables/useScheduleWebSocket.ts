/**
 * useScheduleWebSocket — WebSocket-based scheduling with SSE fallback.
 * Mirrors the C# RunScheduleViaSocketAsync → RunScheduleViaHttpAsync fallback pattern.
 */

import { ref } from 'vue';

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
  token: string;
  onProgress?: (p: ScheduleProgress) => void;
  signal?: AbortSignal;
}

function getWsBaseUrl(baseUrl: string): string {
  // baseUrl is like http://127.0.0.1:8765
  return baseUrl.replace(/^http/, 'ws') + '/api/v1/duty/live';
}

async function runScheduleSSE(
  opts: Omit<RunScheduleOptions, 'onProgress'>,
  onProgress?: (p: ScheduleProgress) => void,
): Promise<ScheduleResult> {
  const { baseUrl, token, instruction, signal } = opts;

  const response = await fetch(`${baseUrl}/api/v1/duty/schedule`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
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
      const lines = buffer.split('\n');
      buffer = lines.pop()!;

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) {
          // Empty line = end of event
          if (currentEvent === 'complete') {
            if (dataBuffer) {
              try {
                finalResult = JSON.parse(dataBuffer) as ScheduleResult;
              } catch { /* ignore parse error */ }
            }
            return finalResult ?? { status: 'error', message: 'No result from SSE stream' };
          }
          if (currentEvent === 'message' || currentEvent === '') {
            if (dataBuffer) {
              try {
                const evt = JSON.parse(dataBuffer);
                onProgress?.({ phase: evt.phase ?? '', message: evt.message ?? '' });
              } catch { /* ignore */ }
            }
          }
          currentEvent = '';
          dataBuffer = '';
          continue;
        }

        if (trimmed.startsWith('event: ')) {
          currentEvent = trimmed.slice(7).trim();
          continue;
        }

        if (trimmed.startsWith('data: ')) {
          dataBuffer = trimmed.slice(6);
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
      // --- WebSocket primary path ---
      const wsUrl = getWsBaseUrl(baseUrl);
      const clientChangeId = crypto.randomUUID().replace(/-/g, '');
      const traceId = `fe-${Date.now()}`;

      let ws: WebSocket | null = null;
      let wsDone = false;

      const wsResult = await new Promise<ScheduleResult>((resolve, reject) => {
        ws = new WebSocket(wsUrl);

        ws.onopen = () => {
          // Send hello handshake
          ws!.send(JSON.stringify({
            type: 'hello',
            trace_id: traceId,
            request_source: 'web_ui',
          }));
        };

        ws.onmessage = (evt) => {
          try {
            const msg = JSON.parse(evt.data as string);

            switch ((msg.type || '').trim().toLowerCase()) {
              case 'hello': {
                // Handshake complete — send schedule_run
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
                // Scheduling accepted, waiting for progress
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
                resolve(result);
                break;
              }
              case 'schedule_cancelled': {
                wsDone = true;
                resolve({ status: 'cancelled', message: '排班执行已取消' });
                break;
              }
              case 'error': {
                wsDone = true;
                const errMsg = (msg.message || 'Unknown error') as string;
                resolve({ status: 'error', message: errMsg });
                break;
              }
              default: {
                // Ignore other message types
                break;
              }
            }
          } catch (e) {
            // Ignore parse errors
          }
        };

        ws.onerror = () => {
          if (!wsDone) {
            wsDone = true;
            reject(new Error('WebSocket connection error'));
          }
        };

        ws.onclose = () => {
          if (!wsDone) {
            wsDone = true;
            reject(new Error('WebSocket closed unexpectedly'));
          }
        };

        // Timeout: if not connected within 5s, fall back to SSE
        setTimeout(() => {
          if (!wsDone && ws && ws.readyState !== WebSocket.OPEN) {
            try { ws.close(); } catch { /* ignore */ }
            reject(new Error('WS_TIMEOUT'));
          }
        }, 5000);
      });

      return wsResult;
    } catch (wsErr: unknown) {
      const wsErrMsg = wsErr instanceof Error ? wsErr.message : String(wsErr);

      // Fall back to SSE if WebSocket failed
      if (wsErrMsg === 'WS_TIMEOUT' || wsErrMsg.includes('WebSocket') || wsErrMsg.includes('connection')) {
        try {
          const sseResult = await runScheduleSSE(
            { baseUrl, token, instruction, signal: abortController.signal },
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
