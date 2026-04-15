import { ref } from 'vue';
import { api } from '@/api/http';
import type { RosterPerson } from '@/types';

export function useRoster() {
  const roster = ref<RosterPerson[]>([]);
  const loading = ref(false);
  const error = ref<string | null>(null);

  const fetch = async () => {
    loading.value = true;
    error.value = null;
    try {
      roster.value = await api.getRoster();
    } catch (e) {
      error.value = String(e);
    } finally {
      loading.value = false;
    }
  };

  const update = async (persons: RosterPerson[]) => {
    await api.updateRoster(persons);
    roster.value = persons;
  };

  return {
    roster,
    loading,
    error,
    fetch,
    update,
  };
}
