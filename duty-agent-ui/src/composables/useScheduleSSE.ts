/**
 * useScheduleSSE — SSE-based scheduling (fallback path).
 * Mirrors the C# RunScheduleViaHttpAsync logic.
 */

import { ref } from 'vue';
import type { ScheduleProgress, ScheduleResult } from './useScheduleWebSocket';

export interface RunScheduleSSEOptions {
  instruction: string;
  baseUrl: string;
  token: string;
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
    isRunning.value = true;
    currentPhase.value = '';
    progress.value = '';
    error.value = null;

    try {
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
        const lines = buffer.split('\n');
        buffer = lines.pop()!;

        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed) {
            if (currentEvent === 'complete') {
              if (dataBuffer) {
                try {
                  finalResult = JSON.parse(dataBuffer) as ScheduleResult;
                } catch { /* ignore */ }
              }
              return finalResult ?? { status: 'error', message: 'SSE stream: no result' };
            }
            if ((currentEvent === 'message' || currentEvent === '') && dataBuffer) {
              try {
                const evt = JSON.parse(dataBuffer);
                const phase = evt.phase ?? '';
                const msg = evt.message ?? '';
                currentPhase.value = phase;
                progress.value = msg;
                onProgress?.({ phase, message: msg, stream_chunk: evt.stream_chunk ?? null });
              } catch { /* ignore */ }
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
