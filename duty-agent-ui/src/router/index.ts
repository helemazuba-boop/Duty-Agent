import { createRouter, createWebHashHistory } from 'vue-router';

const routes = [
  {
    path: '/',
    component: () => import('@/components/layout/AppLayout.vue'),
    children: [
      { path: '', redirect: '/dashboard' },
      {
        path: 'dashboard',
        name: 'Dashboard',
        component: () => import('@/pages/Dashboard.vue'),
      },
      {
        path: 'arrangement',
        name: 'Arrangement',
        component: () => import('@/pages/ArrangementPage.vue'),
      },
      {
        path: 'roster',
        name: 'Roster',
        component: () => import('@/pages/RosterPage.vue'),
      },
      {
        path: 'schedule',
        name: 'Schedule',
        component: () => import('@/pages/SchedulePage.vue'),
      },
      {
        path: 'settings',
        name: 'Settings',
        component: () => import('@/pages/SettingsPage.vue'),
      },
    ],
  },
];

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
});
