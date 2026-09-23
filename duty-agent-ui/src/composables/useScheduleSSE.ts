/**
 * useScheduleSSE — SSE-based scheduling (fallback path).
 * Mirrors the C# RunScheduleViaHttpAsync logic.
 */

import { ref } from 'vue';
import { apiUrl } from '@/api/baseUrl';
import { getToken } from '@/api/http';
import { splitSSEChunk, parseEventName, parseEventData, safeParse } from '@/utils/sseParser';
import type { ScheduleProgress, ScheduleResult } from './useScheduleWebSocket';

export interface RunScheduleSSEOptions {
  instruction: string;
  baseUrl: string;
  /** 可选：缺省时从 http.ts 的共享 getToken() 取（host 注入 token 的单源）。 */
  token?: string;
  onProgress?: (p: ScheduleProgress) => void;
  signal?: AbortSignal;
}

export function useScheduleSSE() {
  const isRunning = ref(false);
  const currentPhase = ref('');
  const progress = ref('');
  const error = ref<string | null>(null);

  async function runSchedule(opts: RunScheduleSSEOptions): Promise<ScheduleResult> {
    const { baseUrl, token, instruction, onProgress, signal } = opts;
    const authToken = token || getToken() || '';
    isRunning.value = true;
    currentPhase.value = '';
    progress.value = '';
    error.value = null;

    try {
      const response = await fetch(
        baseUrl ? `${baseUrl}/api/v1/duty/schedule` : apiUrl('/api/v1/duty/schedule'),
        {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${authToken}`,
        },
        body: JSON.stringify({ instruction }),
        signal,
        },
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let currentEvent = '';
      let dataBuffer = '';
      let finalResult: ScheduleResult | null = null;

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
              return finalResult ?? { status: 'error', message: 'SSE stream: no result' };
            }
            if ((currentEvent === 'message' || currentEvent === '') && dataBuffer) {
              const evt = safeParse<{ phase?: string; message?: string; stream_chunk?: string | null }>(dataBuffer);
              if (evt) {
                const phase = evt.phase ?? '';
                const msg = evt.message ?? '';
                currentPhase.value = phase;
                progress.value = msg;
                onProgress?.({ phase, message: msg, stream_chunk: evt.stream_chunk ?? null });
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

      return finalResult ?? { status: 'error', message: 'SSE stream ended without completion event' };
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      error.value = msg;
      return { status: 'error', message: msg };
    } finally {
      isRunning.value = false;
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
