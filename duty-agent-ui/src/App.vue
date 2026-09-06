<script setup lang="ts">
import { RouterView } from 'vue-router';
import { ConfigProvider } from 'ant-design-vue';
import zhCN from 'ant-design-vue/es/locale/zh_CN';
import { useTheme } from '@/theme/useTheme';

const { antdTheme } = useTheme();
</script>

<template>
  <ConfigProvider :locale="zhCN" :theme="antdTheme">
    <RouterView />
  </ConfigProvider>
</template>
