import { createRouter, createWebHashHistory } from 'vue-router';
import { isLoggedIn } from '../store/auth.js';

const routes = [
  { path: '/login', name: 'login', component: () => import('../views/LoginView.js'), meta: { public: true } },
  { path: '/', name: 'dashboard', component: () => import('../views/DashboardView.js') },
  { path: '/commands', name: 'commands', component: () => import('../views/CommandsView.js') },
  { path: '/workflows', name: 'workflows', component: () => import('../views/WorkflowsView.js') },
  { path: '/remote-hosts', name: 'remote-hosts', component: () => import('../views/RemoteHostsView.js') },
  { path: '/terminal', name: 'terminal', component: () => import('../views/TerminalView.js') },
  { path: '/schedules', name: 'schedules', component: () => import('../views/SchedulesView.js') },
  { path: '/system', name: 'system', component: () => import('../views/SystemStatusView.js') },
  { path: '/services', name: 'services', component: () => import('../views/ServicesView.js') },
  { path: '/workers', name: 'workers', component: () => import('../views/WorkersView.js') },
  { path: '/files', name: 'files', component: () => import('../views/FilesView.js') },
  { path: '/mounts', name: 'mounts', component: () => import('../views/SmbMountsView.js') },
  { path: '/backup-sync', name: 'backup-sync', component: () => import('../views/BackupSyncView.js') },
  { path: '/transcode', name: 'transcode', component: () => import('../views/TranscodeView.js') },
  { path: '/gateway', name: 'gateway', component: () => import('../views/GatewayView.js') },
  { path: '/frp', name: 'frp', component: () => import('../views/FrpView.js') },
  { path: '/easytier', name: 'easytier', component: () => import('../views/EasyTierView.js') },
  { path: '/keys', name: 'keys', component: () => import('../views/ApiKeysView.js') },
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
