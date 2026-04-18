import axios, { type AxiosInstance } from 'axios';
import type { Workspace, ScheduleEntry, RosterPerson } from '@/types';

// Token is injected by main.ts interceptor; expose getToken/setToken for WebSocket composables
export function setToken(t: string) {
  localStorage.setItem('duty_access_token', t);
}
export function getToken() {
  // Prefer dev token, fallback to persisted token
  return (window as any).__DEV_TOKEN__ ?? localStorage.getItem('duty_access_token') ?? '';
}

const http: AxiosInstance = axios.create({
  // Use relative URL so requests go through Vite proxy in dev,
  // and match the app's mounted path in desktop (http://127.0.0.1:8765/app -> relative /api/* resolves correctly)
  baseURL: '',
  timeout: 30000,
});

export const api = {
  async getSnapshot(): Promise<Workspace> {
    const { data } = await http.get<Workspace>('/api/v1/snapshot');
    return data;
  },

  async getConfig() {
    const { data } = await http.get('/api/v1/config');
    return data;
  },
  async updateConfig(config: any) {
    const { data } = await http.patch('/api/v1/config', config);
    return data;
  },

  async getRoster(): Promise<RosterPerson[]> {
    const { data } = await http.get<RosterPerson[]>('/api/v1/roster');
    return data;
  },
  async updateRoster(roster: RosterPerson[]) {
    const { data } = await http.put<RosterPerson[]>('/api/v1/roster', { roster });
    return data;
  },

  async saveScheduleEntry(entry: ScheduleEntry) {
    const { data } = await http.post('/api/v1/duty/schedule-entry', entry);
    return data;
  },
};

export default http;
