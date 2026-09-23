<script setup lang="ts">
/**
 * Panel — antd Card 薄封装（收敛线）。
 * props/slots 与旧自绘版兼容：title/subtitle/actions/padded。
 * 容器（背景/描边/圆角/阴影）走 ConfigProvider theme/tokens.ts，
 * 只保留 subtitle 排版与 actions 布局两段定制 CSS。
 */
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    title?: string;
    subtitle?: string;
    padded?: boolean;
  }>(),
  { padded: true },
);

const bodyStyle = computed(() =>
  props.padded === false ? { padding: 0 } : { padding: '16px 20px 20px' },
);
</script>

<template>
  <a-card class="da-panel" :bordered="true" :body-style="bodyStyle">
    <template v-if="title || $slots.title" #title>
      <slot name="title">
        <span class="da-panel__title">{{ title }}</span>
      </slot>
      <p v-if="subtitle" class="da-panel__subtitle">{{ subtitle }}</p>
    </template>
    <template v-if="$slots.actions" #extra>
      <div class="da-panel__actions">
        <slot name="actions" />
      </div>
    </template>
    <slot />
  </a-card>
</template>

<style scoped>
.da-panel {
  min-width: 0;
}

.da-panel__title {
  font-size: 16px;
  line-height: 24px;
  font-weight: 600;
}

.da-panel__subtitle {
  margin: 2px 0 0;
  font-size: 12px;
  line-height: 16px;
  font-weight: 400;
  color: var(--dt-text-2);
  white-space: normal;
}

.da-panel__actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
