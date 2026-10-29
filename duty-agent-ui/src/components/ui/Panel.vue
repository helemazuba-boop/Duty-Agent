<script setup lang="ts">
/**
 * Panel — 三层卡片体系的第二层。
 *
 * 视觉契约锁定在 2026-09-12 版本:
 *   - 浅色:透明描边 + 柔和阴影,靠"浮起"分层;深色:8% 白细线,无阴影。
 *   - 标题区与正文之间没有分隔线,只靠留白分层。
 *   - 标题区 14px 上留白,正文紧随其后 12px;无标题时正文 16px。
 *
 * 2026-09-24 把自绘 <section> 换成 a-card 时,这三条被 antd 的默认样式顶掉了
 * (可见描边、.ant-card-head 分隔线、56px 固定头高),于是同一屏上 StatCard
 * 还是阴影卡片、Panel 已经退回边框卡片。这里用 :deep 把契约盖回来,结构继续
 * 用 a-card,免得动到依赖 body-style / extra 插槽的调用方。
 *
 * 注意:覆盖 antd 组件根节点必须用 scoped 选择器(未分层、0-2-0),
 * 不能用 Tailwind 工具类 —— 详见 styles/index.css 的层级说明。
 */
import { computed, useSlots } from 'vue';

const props = withDefaults(
  defineProps<{
    title?: string;
    subtitle?: string;
    padded?: boolean;
  }>(),
  { padded: true },
);

const slots = useSlots();
const hasHead = computed(() => Boolean(props.title || slots.title || slots.actions));

// 与 9/12 一致:标题区自带 14px 上留白,正文再跟 12px;无标题时正文 16px。
const bodyStyle = computed(() => {
  if (props.padded === false) return { padding: '0' };
  return { padding: hasHead.value ? '12px 20px 20px' : '16px 20px 20px' };
});
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
  /* 契约:浅色透明描边(靠阴影分层),深色 8% 白细线;两者都由 token 决定 */
  border-color: var(--dt-card-border);
  box-shadow: var(--dt-shadow-card);
}

/* antd 的 head 是 56px 固定高 + 一条分隔线;契约里没有分隔线,头高由内容决定 */
.da-panel :deep(.ant-card-head) {
  min-height: 0;
  margin-bottom: 0;
  padding: 14px 20px 0;
  border-bottom: 0;
  font-size: 16px;
  line-height: 24px;
  color: var(--dt-text);
}

/* 标题区要能跟着副标题换行,关掉 antd 的单行省略 */
.da-panel :deep(.ant-card-head-title) {
  white-space: normal;
  overflow: visible;
  text-overflow: clip;
}

.da-panel :deep(.ant-card-extra) {
  margin-inline-start: 12px;
  padding: 0;
}

.da-panel__title {
  font-size: 16px;
  line-height: 24px;
  font-weight: 600;
  color: var(--dt-text);
}

.da-panel__subtitle {
  /* 9/12 基准:2px + 16px 行高。标题行高 24 自带上下各 4px 行距,视觉上够开。 */
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
  flex-shrink: 0;
}
</style>
