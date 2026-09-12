<script setup lang="ts">
/**
 * PageHeader — 每页自带页头:标题 + 副标题 + 右侧实时状态/操作区。
 * 取消全局 Header 后由各页面使用;sticky 置顶。
 *
 * 滚动感知:页面在顶部时页头透明,画布顶部的光晕能透上来(否则会裁出一个
 * 比周围更暗的矩形);一旦滚动,立刻给近实底色,滑过的内容不穿帮。
 */
import { onBeforeUnmount, onMounted, ref } from 'vue';

defineProps<{
  title: string;
  subtitle?: string;
}>();

const rootEl = ref<HTMLElement | null>(null);
const sentinelEl = ref<HTMLElement | null>(null);
const scrolled = ref(false);
let scroller: HTMLElement | null = null;
let observer: IntersectionObserver | null = null;

const onScroll = () => {
  scrolled.value = (scroller?.scrollTop ?? 0) > 2;
};

onMounted(() => {
  // 滚动容器是布局层的 .app-main,页头只随它吸顶
  scroller = rootEl.value?.closest('.app-main') as HTMLElement | null;
  scroller?.addEventListener('scroll', onScroll, { passive: true });
  // 兜底:滚动事件偶尔不达(宿主合帧节流等)时,靠哨兵离开可视区判定
  observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) scrolled.value = !entry.isIntersecting;
    },
    { root: scroller, threshold: 0 },
  );
  if (sentinelEl.value) observer.observe(sentinelEl.value);
  onScroll();
});

onBeforeUnmount(() => {
  scroller?.removeEventListener('scroll', onScroll);
  observer?.disconnect();
  observer = null;
  scroller = null;
});
</script>

<template>
  <!-- 哨兵:1px 占位,滚出可视区即视为"已滚动" -->
  <div ref="sentinelEl" class="da-page-header__sentinel" aria-hidden="true" />
  <header
    ref="rootEl"
    class="da-page-header"
    :class="{ 'da-page-header--scrolled': scrolled }"
  >
    <div class="da-page-header__text">
      <h1 class="da-page-header__title">{{ title }}</h1>
      <p v-if="subtitle || $slots.subtitle" class="da-page-header__subtitle">
        <slot name="subtitle">{{ subtitle }}</slot>
      </p>
    </div>
    <div class="da-page-header__actions">
      <slot name="actions" />
    </div>
  </header>
</template>

<style scoped>
.da-page-header__sentinel {
  height: 1px;
}

.da-page-header {
  position: sticky;
  top: 0;
  z-index: 5;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  min-height: 56px;
  padding: 4px 0 12px;
  /* 顶部透明;不能用 backdrop-filter——透明底 WebView2 上会把采样区合成成黑块 */
  background: transparent;
  transition: background 0.2s ease;
}

.da-page-header--scrolled {
  /* 滚动后近实底:光晕隐约可透,滑过的内容基本被盖住 */
  background: color-mix(in srgb, var(--dt-bg) 94%, transparent);
}

.da-page-header__text {
  min-width: 0;
}

.da-page-header__title {
  margin: 0;
  font-size: 24px;
  line-height: 32px;
  font-weight: 600;
  letter-spacing: -0.01em;
  color: var(--dt-text);
}

.da-page-header__subtitle {
  margin: 2px 0 0;
  font-size: 13px;
  line-height: 18px;
  color: var(--dt-text-2);
}

.da-page-header__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}
</style>
