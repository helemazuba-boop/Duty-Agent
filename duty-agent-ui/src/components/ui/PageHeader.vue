<script setup lang="ts">
/**
 * PageHeader — 每页自带页头:标题 + 副标题 + 右侧实时状态/操作区。
 * 取消全局 Header 后由各页面使用;sticky 置顶。
 */
defineProps<{
  title: string;
  subtitle?: string;
}>();
</script>

<template>
  <header class="da-page-header">
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
.da-page-header {
  position: sticky;
  top: 0;
  z-index: 5;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  min-height: 56px;
  padding: 8px 0 12px;
  background: var(--dt-bg);
}

.da-page-header__text {
  min-width: 0;
}

.da-page-header__title {
  margin: 0;
  font-size: 20px;
  line-height: 28px;
  font-weight: 600;
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
