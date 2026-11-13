/**
 * useToday — 全局"今天"，唯一事实源是设备本地日历日期（不可被配置改写）。
 * 排班工具会开一整天甚至跨午夜，页面 setup 里算一次 today 会过期；
 * 这里用模块级单例，对齐到整分钟后每分钟刷新，窗口重新可见/获得焦点时补一次。
 *
 * "过了某个时间就显示次日值日"是另一个概念，走 useDutyDisplayDate：
 * 它只选择展示 schedule_pool 里的哪一天，绝不改写 today。两者共用同一个
 * 响应式 now，所以边界翻转和跨午夜翻转都由时钟驱动，与快照何时到达无关。
 *
 * 旧实现把 +1 塞进 dutyToday 且 computed 只依赖 workspace，导致跳变只在
 * 快照恰好重拉时发生；C# 又把后端日期字符串缓存起来 —— 表现为"有概率认错时间"。
 */
import { computed, ref } from 'vue';
import { fmtDate, resolveDisplayDutyDate } from '@/utils/date';

export interface DutyPreviewRule {
  /** 切换边界 HH:MM */
  boundary: string;
  /** 是否启用次日值日预告 */
  enabled: boolean;
}

const now = ref(new Date());

const tick = () => {
  now.value = new Date();
};

// 先对齐到下一整分钟，保证 HH:MM:00 立刻翻转；之后固定 60s 一次。
const initialDelay = 60_000 - (Date.now() % 60_000);
setTimeout(() => {
  tick();
  setInterval(tick, 60_000);
}, initialDelay);

if (typeof document !== 'undefined') {
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') tick();
  });
}
if (typeof window !== 'undefined') {
  window.addEventListener('focus', tick);
}

/** 设备本地日历日期（响应式）。 */
const today = computed(() => fmtDate(now.value));

export function useToday() {
  return today;
}

/** "今天"的值日语义别名。日历/排班/规则一律用它，不随预览切换。 */
export function useDutyToday() {
  return { dutyToday: today };
}

/**
 * 值日展示用日期。rule 必须是响应式的 getter（通常读 workspace 快照）。
 * displayDutyDate 同时依赖 now 与 rule，因此边界到达 / 配置变更都能立即生效。
 */
export function useDutyDisplayDate(rule: () => DutyPreviewRule) {
  const displayDutyDate = computed(() => {
    const { boundary, enabled } = rule();
    return resolveDisplayDutyDate(now.value, boundary, enabled);
  });
  const isPreview = computed(() => displayDutyDate.value !== today.value);
  return { displayDutyDate, isPreview };
}
