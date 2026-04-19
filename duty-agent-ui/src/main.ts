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

// --- App bootstrap ---
import { createApp } from 'vue';
import { createPinia } from 'pinia';
import Antd from 'ant-design-vue';
import App from './App.vue';
import { router } from './router';
import 'ant-design-vue/dist/reset.css';
import './style.css';

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(Antd);
app.mount('#app');
