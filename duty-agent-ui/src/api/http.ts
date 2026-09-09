import axios, { type AxiosInstance } from 'axios';
import { message } from 'ant-design-vue';
import type { Workspace, ScheduleEntry, RosterPerson } from '@/types';
import { API_BASE_URL } from './baseUrl';
import { getToken as getStoredToken, setToken as setStoredToken } from '../tokenStore';

function getAccessToken(): string {
  // Stored token first: it comes from the host-provided URL token and tracks
  // the running backend. __DEV_TOKEN__ is dev-server-only injection and may
  // be stale relative to the current backend session.
  return getStoredToken() ?? (window as any).__DEV_TOKEN__ ?? '';
}

export function setToken(t: string) {
  setStoredToken(t);
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

export interface NotificationSettings {
  version: number;
  notification_entry: 'system' | 'classisland' | 'both' | 'off';
  system_notifications_enabled: boolean;
  schedule_completion_notification_enabled: boolean;
  auto_run_trigger_notification_enabled: boolean;
  duty_reminder_enabled: boolean;
  duty_reminder_times: string[];
  notification_duration_seconds: number;
  auto_run_mode: 'Off' | 'Weekly' | 'Monthly' | 'Custom';
  auto_run_parameter: string;
  auto_run_time: string;
  auto_run_retry_times: number;
  client_auto_start: boolean;
  client_close_action: 'ask' | 'tray' | 'exit';
}

export type NotificationSettingsPatch = Partial<Omit<NotificationSettings, 'version'>> & {
  expected_version?: number;
};

export interface ReadinessCheck {
  id: string;
  ok: boolean;
  detail: string;
  fix: string;
  warn?: boolean;
}

export interface Readiness {
  ready: boolean;
  checks: ReadinessCheck[];
  next_steps: string[];
}

export interface ModelProbeResult {
  ok: boolean;
  status: string;
  detail: string;
  fix: string;
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
  async getReadiness(probeModel = false): Promise<Readiness> {
    const { data } = await http.get<Readiness>('/api/v1/readiness', {
      params: probeModel ? { probe_model: true } : undefined,
    });
    return data;
  },
  async probeModel(payload: { base_url: string; model: string; api_key?: string }): Promise<ModelProbeResult> {
    const { data } = await http.post<ModelProbeResult>('/api/v1/duty/model-probe', payload);
    return data;
  },
  async getNotificationSettings(): Promise<NotificationSettings> {
    const { data } = await http.get<NotificationSettings>('/api/v1/notifications/settings');
    return data;
  },
  async updateNotificationSettings(settings: NotificationSettingsPatch): Promise<NotificationSettings> {
    const { data } = await http.patch<NotificationSettings>('/api/v1/notifications/settings', settings);
    return data;
  },
  async testNotification() {
    const { data } = await http.post('/api/v1/notifications/test', {
      title: 'Duty-Agent 通知测试',
      body: '独立客户端系统通知已连接。',
      route: '/settings',
    });
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
