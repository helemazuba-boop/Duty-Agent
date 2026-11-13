/**
 * useToday — 全局"今天"，唯一事实源是设备本地日历日期。
 * 排班工具会开一整天甚至跨午夜，页面 setup 里算一次 today 会过期；
 * 这里用模块级单例，每分钟、窗口重新可见/获得焦点时刷新。
 *
 * useDutyToday — 值日显示/提醒用的"今天"，与 useToday 完全同源。
 *
 * 旧实现读取 component_refresh_time，时钟越过该时间就把日期 +1 天，
 * 于是"今日值日"在一天里的大部分时间显示的是明天；而且 dutyToday 是个
 * 只依赖 workspace 的 computed，不随时钟更新，+1 只会在快照恰好重新拉取时
 * 发生 —— 表现为"客户端有概率认错时间"。现在直接复用响应式的 today，
 * 跨午夜由同一个 tick 驱动，所有页面同时翻转。
 */
import { ref } from 'vue';
import { fmtDate } from '@/utils/date';

const today = ref(fmtDate(new Date()));

const tick = () => {
  const next = fmtDate(new Date());
  if (next !== today.value) today.value = next;
};

// 跨午夜/睡眠恢复后最多 60s 内翻转；visibilitychange 与 focus 立即补一次。
setInterval(tick, 60_000);
if (typeof document !== 'undefined') {
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') tick();
  });
}
if (typeof window !== 'undefined') {
  window.addEventListener('focus', tick);
}

export function useToday() {
  return today;
}

/** "今日值日"的日期。与 useToday 同值；保留独立命名避免调用方混用语义。 */
export function useDutyToday() {
  return { dutyToday: today };
}
