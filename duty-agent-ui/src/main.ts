import { createApp } from 'vue';
import { createPinia } from 'pinia';
import Antd from 'ant-design-vue';
import App from './App.vue';
import { router } from './router';
import 'ant-design-vue/dist/reset.css';
import './style.css';

// --- Token: dev script sets window.__DEV_TOKEN__, C# plugin uses URL hash ---
function getAccessTokenFromHash(): string | null {
  const hash = window.location.hash;
  const match = hash.match(/[#&]access_token=([^&]+)/);
  return match ? decodeURIComponent(match[1]) : null;
}

const autoToken = (window as any).__DEV_TOKEN__ ?? getAccessTokenFromHash();
if (autoToken) {
  localStorage.setItem('duty_access_token', autoToken);
}

// --- Axios Bearer interceptor ---
import axios from 'axios';
import { message } from 'ant-design-vue';
axios.interceptors.request.use((config) => {
  // Prefer window.__DEV_TOKEN__ (refreshed on every dev server start),
  // fallback to localStorage (persists across reloads but stale after backend restart)
  const token = (window as any).__DEV_TOKEN__ ?? localStorage.getItem('duty_access_token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

// --- Global error handler: show user-friendly toast for API failures ---
axios.interceptors.response.use(
  (response) => response,
  (error) => {
    if (!error.response) {
      // Network error (CORS, timeout, server down)
      message.error('网络错误，请检查后端服务是否运行');
      return Promise.reject(error);
    }

    const status = error.response.status;

    if (status === 401) {
      message.error('登录已过期，请刷新页面重新登录');
    } else if (status === 403) {
      message.error('无权限执行此操作');
    } else if (status === 404) {
      // Let caller handle 404 (might be expected "not found" state)
      // Only warn for routes that should exist
      const path = error.config?.url ?? '';
      if (!path.includes('/not-found')) {
        message.warning('请求的资源不存在');
      }
    } else if (status >= 500) {
      message.error('服务器错误，请稍后重试');
    }
    return Promise.reject(error);
  }
);

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(Antd);
app.mount('#app');
