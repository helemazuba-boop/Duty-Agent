<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';
import { Layout, Menu, Typography } from 'ant-design-vue';
import {
  CalendarOutlined,
  DashboardOutlined,
  ExperimentOutlined,
  SettingOutlined,
  TeamOutlined,
} from '@ant-design/icons-vue';

const { Sider, Content } = Layout;
const { Title } = Typography;
const route = useRoute();
const selectedMenuKeys = computed(() => [route.path.split('/')[1] || 'dashboard']);
</script>

<template>
  <Layout class="app-shell">
    <Sider class="app-sidebar" :width="220">
      <div class="app-brand">
        <Title :level="4" class="app-brand-title">排班管理</Title>
      </div>

      <Menu mode="inline" theme="dark" class="app-menu" :selected-keys="selectedMenuKeys">
        <Menu.Item key="dashboard">
          <template #icon><DashboardOutlined /></template>
          <RouterLink to="/dashboard">首页仪表盘</RouterLink>
        </Menu.Item>
        <Menu.Item key="arrangement">
          <template #icon><CalendarOutlined /></template>
          <RouterLink to="/arrangement">排班安排</RouterLink>
        </Menu.Item>
        <Menu.Item key="roster">
          <template #icon><TeamOutlined /></template>
          <RouterLink to="/roster">花名册管理</RouterLink>
        </Menu.Item>
        <Menu.Item key="schedule">
          <template #icon><ExperimentOutlined /></template>
          <RouterLink to="/schedule">AI 排班</RouterLink>
        </Menu.Item>
        <Menu.Item key="settings">
          <template #icon><SettingOutlined /></template>
          <RouterLink to="/settings">设置</RouterLink>
        </Menu.Item>
      </Menu>
    </Sider>

    <Layout class="app-main">
      <Content class="app-content">
        <RouterView />
      </Content>
    </Layout>
  </Layout>
</template>

<style scoped>
.app-shell {
  min-height: 100vh;
  width: 100vw;
  overflow: hidden;
}

.app-sidebar {
  background: #001529;
  position: fixed;
  inset: 0 auto 0 0;
  overflow: auto;
  z-index: 10;
}

.app-brand {
  height: 64px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.app-brand-title {
  color: #fff !important;
  margin: 0 !important;
  letter-spacing: 2px;
}

.app-menu {
  border-right: 0;
  margin-top: 8px;
}

.app-main {
  margin-left: 220px;
  min-width: 0;
  width: calc(100vw - 220px);
  height: 100vh;
  overflow: hidden;
}

.app-content {
  min-width: 0;
  height: 100vh;
  overflow: auto;
  padding: 24px;
  background: #f0f2f5;
}
</style>
