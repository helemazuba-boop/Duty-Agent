<script setup lang="ts">
/**
 * Panel — 三层卡片体系的第二层:白底 + 1px 边框 + 圆角 10,无阴影。
 * 替代裸 a-card;面板内分区用 surface-2 底,禁止卡中套卡。
 */
defineProps<{
  title?: string;
  subtitle?: string;
  padded?: boolean;
}>();
</script>

<template>
  <section class="da-panel">
    <header v-if="title || $slots.title || $slots.actions" class="da-panel__header">
      <div class="da-panel__heading">
        <slot name="title">
          <h3 class="da-panel__title">{{ title }}</h3>
        </slot>
        <p v-if="subtitle" class="da-panel__subtitle">{{ subtitle }}</p>
      </div>
      <div v-if="$slots.actions" class="da-panel__actions">
        <slot name="actions" />
      </div>
    </header>
    <div class="da-panel__body" :class="{ 'da-panel__body--flush': padded === false }">
      <slot />
    </div>
  </section>
</template>

<style scoped>
.da-panel {
  background: var(--dt-surface);
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius-lg);
  min-width: 0;
}

.da-panel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 20px 0;
}

.da-panel__heading {
  min-width: 0;
}

.da-panel__title {
  margin: 0;
  font-size: 16px;
  line-height: 24px;
  font-weight: 600;
  color: var(--dt-text);
}

.da-panel__subtitle {
  margin: 2px 0 0;
  font-size: 12px;
  line-height: 16px;
  color: var(--dt-text-2);
}

.da-panel__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.da-panel__body {
  padding: 16px 20px 20px;
}

.da-panel__body--flush {
  padding: 0;
}

/* 无 header 时给 body 一个等距上边距 */
.da-panel__header + .da-panel__body {
  padding-top: 12px;
}
</style>
