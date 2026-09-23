import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import { api } from '@/api/http';
import type { RosterPerson, Workspace } from '@/types';
import { snapshotKey, rosterKey } from './keys';
import { queryClient } from './client';

export const SNAPSHOT_POLL_MS = 20_000;

export function useSnapshotQuery(pollMs: number = SNAPSHOT_POLL_MS) {
  return useQuery<Workspace, unknown>({
    queryKey: snapshotKey,
    queryFn: () => api.getSnapshot({ silent: true }),
    refetchInterval: pollMs,
    refetchIntervalInBackground: true,
  });
}

export function useRosterQuery() {
  return useQuery<RosterPerson[], unknown>({
    queryKey: rosterKey,
    queryFn: () => api.getRoster(),
    staleTime: 30_000,
  });
}

export function useUpdateRoster() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (roster: RosterPerson[]) => api.updateRoster(roster),
    onSuccess: (roster) => {
      qc.setQueryData(rosterKey, roster);
      qc.invalidateQueries({ queryKey: snapshotKey });
    },
  });
}

export function invalidateSnapshot() {
  return queryClient.invalidateQueries({ queryKey: snapshotKey });
}
