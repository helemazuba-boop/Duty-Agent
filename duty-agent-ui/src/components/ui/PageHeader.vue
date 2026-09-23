<script setup lang="ts">
/**
 * PageHeader — antd PageHeader 薄封装（收敛线）。
 * slots 与旧自绘版兼容：#subtitle / #actions。
 * sticky 哨兵逻辑抽到 useScrolled，透明 WebView2 禁 backdrop-filter 保持。
 */
import { useScrolled } from '@/composables/useScrolled';

defineProps<{
  title: string;
  subtitle?: string;
}>();

const { rootEl, sentinelEl, scrolled } = useScrolled();
void rootEl;
void sentinelEl;
</script>

<template>
  <div ref="sentinelEl" class="da-page-header__sentinel" aria-hidden="true" />
  <a-page-header
    ref="rootEl"
    class="da-page-header"
    :class="{ 'da-page-header--scrolled': scrolled }"
    :ghost="true"
    :title="title"
    :sub-title="subtitle"
  >
    <template v-if="$slots.subtitle" #subTitle>
      <slot name="subtitle">{{ subtitle }}</slot>
    </template>
    <template v-if="$slots.actions" #extra>
      <slot name="actions" />
    </template>
  </a-page-header>
</template>

<style scoped>
.da-page-header__sentinel {
  height: 1px;
}

.da-page-header {
  position: sticky;
  top: 0;
  z-index: 5;
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

.da-page-header :deep(.ant-page-header-heading) {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 0;
}

.da-page-header :deep(.ant-page-header-heading-title) {
  margin: 0;
  font-size: 24px;
  line-height: 32px;
  font-weight: 600;
  letter-spacing: -0.01em;
  color: var(--dt-text);
  overflow: hidden;
  text-overflow: ellipsis;
}

.da-page-header :deep(.ant-page-header-heading-sub-title) {
  font-size: 13px;
  line-height: 18px;
  color: var(--dt-text-2);
}

.da-page-header :deep(.ant-page-header-heading-extra) {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
  margin: 0;
}
</style>
