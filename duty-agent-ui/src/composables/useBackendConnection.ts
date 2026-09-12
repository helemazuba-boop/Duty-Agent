/**
 * useBackendConnection — 后端连接探活的模块级单例。
 *
 * 全应用唯一的连接状态源（20s 轮询 getSnapshot）：
 * - AppLayout 侧栏状态点 / SettingsPage「连接」面板 / Dashboard 头部状态点全部消费它；
 * - 20s 定时器在这里（AppLayout 只负责 start/stop 生命周期）；
 * - 探活请求走 silent 模式，避免轮询失败时全局 toast 刷屏；
 * - 错误分类：network（后端不可达） vs unauthorized（401 登录失效） vs server（5xx）。
 */
import { ref } from 'vue';
import { api } from '@/api/http';

export type BackendConnStatus = 'pending' | 'ok' | 'error';
export type BackendConnErrorKind = 'network' | 'unauthorized' | 'server' | null;

const POLL_INTERVAL_MS = 20_000;

const status = ref<BackendConnStatus>('pending');
const checking = ref(false);
const errorKind = ref<BackendConnErrorKind>(null);
const lastCheckedAt = ref(0);

let timer: number | undefined;
let started = false;

function classifyError(err: unknown): BackendConnErrorKind {
  const response = (err as { response?: { status?: number } })?.response;
  if (!response) return 'network';
  if (response.status === 401) return 'unauthorized';
  if (typeof response.status === 'number' && response.status >= 500) return 'server';
  return 'server';
}

async function probe(): Promise<void> {
  if (checking.value) return;
  checking.value = true;
  try {
    await api.getSnapshot({ silent: true });
    status.value = 'ok';
    errorKind.value = null;
  } catch (err) {
    status.value = 'error';
    errorKind.value = classifyError(err);
  } finally {
    checking.value = false;
    lastCheckedAt.value = Date.now();
  }
}

/** 立即探活一次（手动“重新检测”按钮与轮询共用）。 */
async function checkNow(): Promise<void> {
  await probe();
}

/** 启动轮询（幂等）：先立即探活，再挂 20s 定时器。 */
function start(): void {
  if (started) return;
  started = true;
  void probe();
  timer = window.setInterval(() => void probe(), POLL_INTERVAL_MS);
}

/** 停止轮询（仅 AppLayout 卸载时调用；正常生命周期下布局常驻）。 */
function stop(): void {
  if (timer !== undefined) {
    window.clearInterval(timer);
    timer = undefined;
  }
  started = false;
}

export function useBackendConnection() {
  return {
    status,
    checking,
    errorKind,
    lastCheckedAt,
    checkNow,
    start,
    stop,
  };
}
