import './bootstrapToken';

import { createApp } from 'vue';
import { createPinia } from 'pinia';
import Antd from 'ant-design-vue';
import App from './App.vue';
import { router } from './router';
import { vFlash } from './directives/flash';
import 'ant-design-vue/dist/reset.css';
import './styles/index.css';

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(Antd);
app.directive('flash', vFlash);
app.mount('#app');
