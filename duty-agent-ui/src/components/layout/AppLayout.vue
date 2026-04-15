<script setup lang="ts">
import { RouterLink, RouterView } from 'vue-router';
import { Layout, Menu, Typography } from 'ant-design-vue';
import {
  DashboardOutlined,
  CalendarOutlined,
  TeamOutlined,
  SettingOutlined,
  ExperimentOutlined,
} from '@ant-design/icons';

const { Sider, Content } = Layout;
const { Title } = Typography;
</script>

<template>
  <Layout style="min-height: 100vh">
    <Sider
      style="
        background: #001529;
        position: fixed;
        left: 0;
        top: 0;
        bottom: 0;
        overflow: auto;
      "
      :width="220"
    >
      <div
        style="
          height: 64px;
          display: flex;
          align-items: center;
          justify-content: center;
          border-bottom: 1px solid rgba(255,255,255,0.1);
        "
      >
        <Title :level="4" style="color: #fff; margin: 0; letter-spacing: 2px">
          排班管理
        </Title>
      </div>

      <Menu mode="inline" theme="dark" style="border-right: 0; margin-top: 8px">
        <Menu.Item key="dashboard" :icon="DashboardOutlined">
          <RouterLink to="/dashboard">首页仪表盘</RouterLink>
        </Menu.Item>
        <Menu.Item key="arrangement" :icon="CalendarOutlined">
          <RouterLink to="/arrangement">排班安排</RouterLink>
        </Menu.Item>
        <Menu.Item key="roster" :icon="TeamOutlined">
          <RouterLink to="/roster">花名册管理</RouterLink>
        </Menu.Item>
        <Menu.Item key="schedule" :icon="ExperimentOutlined">
          <RouterLink to="/schedule">排班方案</RouterLink>
        </Menu.Item>
        <Menu.Item key="settings" :icon="SettingOutlined">
          <RouterLink to="/settings">设置</RouterLink>
        </Menu.Item>
      </Menu>
    </Sider>

    <Layout style="margin-left: 220px">
      <Content style="padding: 24px; background: #f0f2f5; min-height: calc(100vh - 64px)">
        <RouterView />
      </Content>
    </Layout>
  </Layout>
</template>
