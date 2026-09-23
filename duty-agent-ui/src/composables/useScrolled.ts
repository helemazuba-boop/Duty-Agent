import { onMounted, ref } from 'vue';
import { useEventListener, useIntersectionObserver } from '@vueuse/core';

export function useScrolled(options: { selector?: string; threshold?: number } = {}) {
  const { selector = '.app-main', threshold = 2 } = options;
  const rootEl = ref<HTMLElement | null>(null);
  const sentinelEl = ref<HTMLElement | null>(null);
  const scrolled = ref(false);
  const scroller = ref<HTMLElement | null>(null);

  const onScroll = () => {
    scrolled.value = (scroller.value?.scrollTop ?? 0) > threshold;
  };

  onMounted(() => {
    const rootNode = rootEl.value as unknown as HTMLElement | null;
    scroller.value =
      (typeof rootNode?.closest === 'function'
        ? (rootNode.closest(selector) as HTMLElement | null)
        : null)
      ?? (document.querySelector(selector) as HTMLElement | null);
    onScroll();
  });

  useEventListener(scroller, 'scroll', onScroll, { passive: true });
  useIntersectionObserver(
    sentinelEl,
    ([entry]) => {
      if (entry) scrolled.value = !entry.isIntersecting;
    },
    { root: scroller },
  );

  return { rootEl, sentinelEl, scrolled };
}
