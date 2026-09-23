export interface SSEParsedEvent {
  event: string;
  data: string;
}

export function splitSSEChunk(buffer: string): { lines: string[]; rest: string } {
  const lines = buffer.split('\n');
  const rest = lines.pop() ?? '';
  return { lines, rest };
}

export function isEventBoundary(trimmed: string): boolean {
  return trimmed === '';
}

export function parseEventName(trimmed: string): string | null {
  if (trimmed.startsWith('event: ')) return trimmed.slice(7).trim();
  return null;
}

export function parseEventData(trimmed: string): string | null {
  if (trimmed.startsWith('data: ')) return trimmed.slice(6);
  return null;
}

export function safeParse<T>(text: string): T | null {
  try {
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}
