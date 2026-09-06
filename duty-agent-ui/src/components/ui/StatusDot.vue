<script setup lang="ts">
/**
 * StatusDot — 连接/状态圆点:绿=正常 红=异常 黄=中间态。
 * pulse 属性用于收到 WS 消息时脉冲一次。
 */
withDefaults(
  defineProps<{
    status?: 'ok' | 'error' | 'pending' | 'idle';
    label?: string;
    pulse?: boolean;
    size?: number;
  }>(),
  { status: 'idle', size: 8 },
);
</script>

<template>
  <span class="da-status" :class="`da-status--${status}`">
    <span
      class="da-status__dot"
      :class="{ 'da-pulse-once': pulse }"
      :style="{ width: `${size}px`, height: `${size}px` }"
    />
    <span v-if="label" class="da-status__label">{{ label }}</span>
  </span>
</template>

<style scoped>
.da-status {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.da-status__dot {
  flex-shrink: 0;
  border-radius: 999px;
  display: inline-block;
}

.da-status__label {
  font-size: 12px;
  line-height: 16px;
  color: var(--dt-text-2);
  white-space: nowrap;
}

.da-status--ok .da-status__dot {
  background: var(--dt-success);
  color: var(--dt-success);
}

.da-status--error .da-status__dot {
  background: var(--dt-danger);
  color: var(--dt-danger);
}

.da-status--pending .da-status__dot {
  background: var(--dt-warning);
  color: var(--dt-warning);
}

.da-status--idle .da-status__dot {
  background: var(--dt-text-3);
  color: var(--dt-text-3);
}
</style>
