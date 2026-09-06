<script setup lang="ts">
/**
 * StatCard — 自绘 KPI 卡:标签 13px 次级色 → 数字 28px tabular → 底部 12px 对比信息。
 * 不用 AntD Statistic、不放 icon 大色块(那是模板味的来源)。
 * flashKey:传入"数据签名",变化时根元素触发一次环形 flash(v-flash 指令)。
 * to:传入路由地址则整卡渲染为 RouterLink。
 */
import type { Component } from 'vue';

withDefaults(
  defineProps<{
    label: string;
    value: number | string;
    hint?: string;
    tone?: 'default' | 'success' | 'warning' | 'danger' | 'live';
    icon?: Component;
    flashKey?: number | string;
    to?: string;
  }>(),
  { tone: 'default' },
);
</script>

<template>
  <RouterLink v-if="to" :to="to" class="da-stat da-stat--link" :class="`da-stat--${tone}`" v-flash="flashKey">
    <div class="da-stat__label">
      <component :is="icon" v-if="icon" class="da-stat__icon" />
      <span>{{ label }}</span>
    </div>
    <div class="da-stat__value da-tnum">{{ value }}</div>
    <div v-if="hint" class="da-stat__hint">{{ hint }}</div>
  </RouterLink>

  <div v-else class="da-stat" :class="`da-stat--${tone}`" v-flash="flashKey">
    <div class="da-stat__label">
      <component :is="icon" v-if="icon" class="da-stat__icon" />
      <span>{{ label }}</span>
    </div>
    <div class="da-stat__value da-tnum">{{ value }}</div>
    <div v-if="hint" class="da-stat__hint">{{ hint }}</div>
  </div>
</template>

<style scoped>
.da-stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 14px 16px;
  background: var(--dt-surface);
  border: 1px solid var(--dt-border);
  border-radius: var(--dt-radius-lg);
  min-width: 0;
  text-decoration: none;
  transition: border-color 0.15s ease;
}

a.da-stat:hover {
  border-color: var(--dt-border-strong);
}

.da-stat__label {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  line-height: 16px;
  color: var(--dt-text-2);
}

.da-stat__icon {
  font-size: 14px;
  color: var(--dt-text-3);
}

.da-stat__value {
  font-size: 28px;
  line-height: 36px;
  font-weight: 600;
  color: var(--dt-text);
}

/* 12px 是要读的文字,用 text2;text3 只给装饰/禁用 */
.da-stat__hint {
  font-size: 12px;
  line-height: 16px;
  color: var(--dt-text-2);
}

.da-stat--success .da-stat__value { color: var(--dt-success); }
.da-stat--success .da-stat__hint { color: var(--dt-success); }
.da-stat--warning .da-stat__value { color: var(--dt-warning); }
.da-stat--warning .da-stat__hint { color: var(--dt-warning); }
.da-stat--danger .da-stat__value { color: var(--dt-danger); }
.da-stat--live .da-stat__value { color: var(--dt-live); }
</style>
