<script setup lang="ts">
/**
 * PersonChip — 人名 chip:hash 色浅底 + 深字,可选头像圆点,可关闭。
 * neutral=true 用于 "+N" 这类非人名占位(灰底灰字,不上人员色)。
 * 日历、值班条带、方案面板、编辑器全站复用。
 */
import { computed } from 'vue';
import { CloseOutlined } from '@ant-design/icons-vue';
import { personChipStyle, personInitial } from '@/utils/personColor';
import { useTheme } from '@/theme/useTheme';

const props = withDefaults(
  defineProps<{
    name: string;
    avatar?: boolean;
    dot?: boolean;
    danger?: boolean;
    closable?: boolean;
    neutral?: boolean;
  }>(),
  { avatar: false, dot: false, danger: false, closable: false, neutral: false },
);

const emit = defineEmits<{ (e: 'close'): void }>();

const { resolved } = useTheme();

const style = computed(() => (props.neutral ? {} : personChipStyle(props.name, resolved.value)));
const initial = computed(() => personInitial(props.name));
</script>

<template>
  <span
    class="da-person"
    :class="{ 'da-person--danger': danger, 'da-person--neutral': neutral }"
    :style="style"
  >
    <span v-if="avatar" class="da-person__avatar">{{ initial }}</span>
    <span v-else-if="dot" class="da-person__dot" />
    <span class="da-person__name">{{ name }}</span>
    <slot />
    <button
      v-if="closable"
      type="button"
      class="da-person__close"
      :aria-label="`移除 ${name}`"
      @click.stop="emit('close')"
    >
      <CloseOutlined />
    </button>
  </span>
</template>

<style scoped>
.da-person {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 1px 8px;
  border-radius: 999px;
  font-size: 12px;
  line-height: 20px;
  color: var(--person-fg);
  background: var(--person-bg);
  white-space: nowrap;
}

.da-person--neutral {
  color: var(--dt-text-2);
  background: var(--dt-hover);
}

.da-person--danger {
  color: var(--dt-danger);
  background: color-mix(in srgb, var(--dt-danger) 12%, transparent);
}

.da-person__name {
  overflow: hidden;
  text-overflow: ellipsis;
}

.da-person__avatar {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  color: var(--person-on-solid);
  background: var(--person-solid);
  flex-shrink: 0;
}

.da-person__dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--person-solid);
  flex-shrink: 0;
}

.da-person__close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  margin-right: -3px;
  padding: 0;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: inherit;
  font-size: 8px;
  cursor: pointer;
  opacity: 0.7;
  transition: opacity 0.15s ease, background 0.15s ease;
}

.da-person__close:hover {
  opacity: 1;
  background: color-mix(in srgb, currentColor 18%, transparent);
}
</style>
