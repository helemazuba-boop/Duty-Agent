import { createApp } from 'vue';
import { createPinia } from 'pinia';
import Antd from 'ant-design-vue';
import App from './App.vue';
import { router } from './router';
import 'ant-design-vue/dist/reset.css';
import './style.css';

// --- Token auto-read from URL hash ---
// C# plugin injects token as: http://host/app/#access_token=xxx
function getAccessTokenFromHash(): string | null {
  const hash = window.location.hash;
  const match = hash.match(/[#&]access_token=([^&]+)/);
  return match ? decodeURIComponent(match[1]) : null;
}

const autoToken = getAccessTokenFromHash();
if (autoToken) {
  localStorage.setItem('duty_access_token', autoToken);
}

// --- Axios Bearer interceptor ---
import axios from 'axios';
axios.interceptors.request.use((config) => {
  const token = localStorage.getItem('duty_access_token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(Antd);
app.mount('#app');
