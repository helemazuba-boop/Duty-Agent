<script setup lang="ts">
/**
 * PageHeader — 每页自带页头:标题 + 副标题 + 右侧实时状态/操作区。
 *
 * 排版回到 2026-09-12:描述型副标题(「查看和管理每日值日安排」这类)排在标题
 * 下面,标题 24/32、副标题 13/18。9/24 换成 a-page-header 后副标题被 antd 的
 * heading-left 拉到了标题右侧同一行,读起来像标题的后缀。
 *
 * 副标题是状态芯片时(仪表盘那颗连接状态点)同行反而更合适,这类调用方传
 * variant="inline" 保留旧行为。
 *
 * sticky 哨兵逻辑走 useScrolled;顶部透明,禁用 backdrop-filter —— 透明底
 * WebView2 上会把采样区合成成黑块。
 */
import { useScrolled } from '@/composables/useScrolled';

withDefaults(
  defineProps<{
    title: string;
    subtitle?: string;
    variant?: 'stacked' | 'inline';
  }>(),
  { variant: 'stacked' },
);

const { rootEl, sentinelEl, scrolled } = useScrolled();
void rootEl;
void sentinelEl;
</script>

<template>
  <!-- 哨兵:1px 占位,滚出可视区即视为"已滚动" -->
  <div ref="sentinelEl" class="da-page-header__sentinel" aria-hidden="true" />
  <header
    ref="rootEl"
    class="da-page-header"
    :class="[`da-page-header--${variant}`, { 'da-page-header--scrolled': scrolled }]"
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

/* 状态芯片型副标题:跟标题同一行,右侧留 12px */
.da-page-header--inline .da-page-header__text {
  display: flex;
  align-items: center;
  gap: 12px;
}

.da-page-header--inline .da-page-header__title {
  flex-shrink: 0;
}

.da-page-header--inline .da-page-header__subtitle {
  margin: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
