import { useRosterQuery, useUpdateRoster } from '@/queries/useSnapshot';
import type { RosterPerson } from '@/types';

export function useRoster() {
  const rosterQuery = useRosterQuery();
  const updateMutation = useUpdateRoster();

  const update = async (persons: RosterPerson[]) => {
    await updateMutation.mutateAsync(persons);
  };

  return {
    roster: rosterQuery.data,
    loading: rosterQuery.isPending,
    error: rosterQuery.error,
    fetch: () => rosterQuery.refetch(),
    update,
    isFetching: rosterQuery.isFetching,
  };
}
