/**
 * useToday — 全局"今天"。
 * 排班工具会开一整天甚至跨午夜,页面 setup 里算一次 today 会过期;
 * 这里模块级单例,每分钟与 visibilitychange 时刷新。
 *
 * useDutyToday — "当前效值日"，用于值日显示和提醒语义。
 * 默认与系统日期一致；当 workspace 返回 component_refresh_time 后，
 * 如果当前时间已经 >= component_refresh_time，则推进到下一天。
 * 这使前端与 C# DutyScheduleOrchestrator.GetCurrentScheduleDate() 保持一致。
 */
import { ref, computed } from 'vue';
import { fmtDate } from '@/utils/date';
import type { Workspace } from '@/types';

const today = ref(fmtDate(new Date()));

/** 后端返回的 workspace 快照（仅取 component_refresh_time）。 */
const workspaceRef = ref<Workspace | null>(null);

function setWorkspace(ws: Workspace | null) {
  workspaceRef.value = ws;
}

/** 根据 workspace.component_refresh_time 计算当前效值日。 */
function computeDutyDateFromWorkspace(ws: Workspace | null): string {
  const now = new Date();
  const refreshText = ws?.component_refresh_time?.trim() || '08:00';
  let refreshHour = 8;
  let refreshMinute = 0;
  const parts = refreshText.split(':');
  if (parts.length >= 2) {
    const h = parseInt(parts[0], 10);
    const m = parseInt(parts[1], 10);
    if (!Number.isNaN(h) && !Number.isNaN(m) && h >= 0 && h < 24 && m >= 0 && m < 60) {
      refreshHour = h;
      refreshMinute = m;
    }
  }
  const currentMinutes = now.getHours() * 60 + now.getMinutes();
  const refreshMinutes = refreshHour * 60 + refreshMinute;
  const target = new Date(now);
  if (currentMinutes >= refreshMinutes) {
    target.setDate(target.getDate() + 1);
  }
  return fmtDate(target);
}

/** 效值日（受 component_refresh_time 影响）。 */
const dutyToday = computed(() => computeDutyDateFromWorkspace(workspaceRef.value));

const tick = () => {
  const next = fmtDate(new Date());
  if (next !== today.value) today.value = next;
};

setInterval(tick, 60_000);
if (typeof document !== 'undefined') {
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') tick();
  });
}

export function useToday() {
  return today;
}

export function useDutyToday() {
  return { dutyToday, setWorkspace };
}
