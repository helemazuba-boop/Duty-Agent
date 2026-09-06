/**
 * useToday — 全局"今天"。
 * 排班工具会开一整天甚至跨午夜,页面 setup 里算一次 today 会过期;
 * 这里模块级单例,每分钟与 visibilitychange 时刷新。
 */
import { ref } from 'vue';
import { fmtDate } from '@/utils/date';

const today = ref(fmtDate(new Date()));

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
