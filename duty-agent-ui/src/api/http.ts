import axios, { type AxiosInstance } from 'axios';
import { message } from 'ant-design-vue';
import type { Workspace, ScheduleEntry, RosterPerson } from '@/types';
import { API_BASE_URL } from './baseUrl';

function getAccessToken(): string {
  return (window as any).__DEV_TOKEN__ ?? localStorage.getItem('duty_access_token') ?? '';
}

// Token is injected by main.ts interceptor; expose getToken/setToken for WebSocket composables
export function setToken(t: string) {
  localStorage.setItem('duty_access_token', t);
}
export function getToken() {
  return getAccessToken();
}

export interface BridgeStatus {
  status: 'connected' | 'disconnected';
  connected: boolean;
  last_seen_at: number | null;
  last_seen_iso: string | null;
  age_seconds: number | null;
  ttl_seconds: number;
  source: string | null;
}

const http: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  timeout: 30000,
});

http.interceptors.request.use((config) => {
  const token = getAccessToken();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

http.interceptors.response.use(
  (response) => response,
  (error) => {
    if (!error.response) {
      message.error('网络错误，请检查后端服务是否运行');
      return Promise.reject(error);
    }

    const status = error.response.status;

    if (status === 401) {
      message.error('登录已过期，请刷新页面重新登录');
    } else if (status === 403) {
      message.error('无权限执行此操作');
    } else if (status === 404) {
      const path = error.config?.url ?? '';
      if (!path.includes('/not-found')) {
        message.warning('请求的资源不存在');
      }
    } else if (status >= 500) {
      message.error('服务器错误，请稍后重试');
    }
    return Promise.reject(error);
  }
);

export const api = {
  async getSnapshot(): Promise<Workspace> {
    const { data } = await http.get<Workspace>('/api/v1/snapshot');
    return data;
  },

  async getBridgeStatus(): Promise<BridgeStatus> {
    const { data } = await http.get<BridgeStatus>('/api/v1/bridge/status');
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
    const { data } = await http.get<{ roster: RosterPerson[] }>('/api/v1/roster');
    return data.roster;
  },
  async updateRoster(roster: RosterPerson[]) {
    const { data } = await http.put<{ roster: RosterPerson[] }>('/api/v1/roster', { roster });
    return data.roster;
  },

  async saveScheduleEntry(entry: ScheduleEntry) {
    // Handle Dayjs/Day object (from DatePicker) and ensure date is string YYYY-MM-DD
    const fmtDate = (d: any): string => {
      if (!d) return '';
      if (typeof d === 'string') return d.slice(0, 10);
      if (typeof d.format === 'function') return d.format('YYYY-MM-DD');
      return String(d).slice(0, 10);
    };
    const payload = {
      target_date: fmtDate(entry.date),
      day: entry.day?.toString() ?? null,
      area_assignments: entry.area_assignments,
      note: entry.note ?? null,
      confirm_overwrite: true,
      ledger_mode: "record" as const,
    };
    const { data } = await http.post('/api/v1/duty/schedule-entry', payload);
    return data;
  },
};

export default http;
