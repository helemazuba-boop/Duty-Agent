/**
 * useBackendConnection — 后端连接探活（TanStack Query 派生层）。
 *
 * 历史 API 保持兼容：status/checking/errorKind/lastCheckedAt/checkNow/start/stop。
 * 轮询由 useSnapshotQuery 统一持有（20s refetchInterval），不再自维护 setInterval。
 */
import { computed } from 'vue';
import { useSnapshotQuery } from '@/queries/useSnapshot';

export type BackendConnStatus = 'pending' | 'ok' | 'error';
export type BackendConnErrorKind = 'network' | 'unauthorized' | 'server' | null;

function classifyError(err: unknown): BackendConnErrorKind {
  const response = (err as { response?: { status?: number } })?.response;
  if (!response) return 'network';
  if (response.status === 401) return 'unauthorized';
  if (typeof response.status === 'number' && response.status >= 500) return 'server';
  return 'server';
}

export function useBackendConnection() {
  const query = useSnapshotQuery();

  const status = computed<BackendConnStatus>(() => {
    if (query.isPending.value) return 'pending';
    if (query.isError.value) return 'error';
    return 'ok';
  });
  const checking = computed(() => query.isFetching.value);
  const errorKind = computed<BackendConnErrorKind>(() =>
    query.isError.value ? classifyError(query.error.value) : null,
  );
  const lastCheckedAt = computed(() => query.dataUpdatedAt.value);

  async function checkNow(): Promise<void> {
    await query.refetch();
  }

  /** 兼容旧调用：Query 挂载即自动轮询，无需手动启动。 */
  function start(): void {
    void query.refetch();
  }

  /** 兼容旧调用：Query 由 GC 自动回收，卸载无需停表。 */
  function stop(): void {}

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
