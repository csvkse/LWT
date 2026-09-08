import { createRouter, createWebHashHistory } from 'vue-router';
import { isLoggedIn } from '../store/auth.js';

const routes = [
  { path: '/login', name: 'login', component: () => import('../views/LoginView.js'), meta: { public: true } },
  { path: '/', name: 'dashboard', component: () => import('../views/DashboardView.js') },
  { path: '/commands', name: 'commands', component: () => import('../views/CommandsView.js') },
  { path: '/schedules', name: 'schedules', component: () => import('../views/SchedulesView.js') },
  { path: '/system', name: 'system', component: () => import('../views/SystemStatusView.js') },
  { path: '/files', name: 'files', component: () => import('../views/FilesView.js') },
  { path: '/mounts', name: 'mounts', component: () => import('../views/SmbMountsView.js') },
  { path: '/transcode', name: 'transcode', component: () => import('../views/TranscodeView.js') },
  { path: '/history', name: 'history', component: () => import('../views/HistoryView.js') },
  { path: '/logs', name: 'logs', component: () => import('../views/LogsView.js') },
  { path: '/:pathMatch(.*)*', redirect: '/' },
];

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
});

router.beforeEach((to) => {
  if (!to.meta.public && !isLoggedIn()) return { path: '/login' };
  if (to.path === '/login' && isLoggedIn()) return { path: '/' };
  return true;
});
