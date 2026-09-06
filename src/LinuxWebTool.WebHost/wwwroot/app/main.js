import { createApp } from 'vue';
import AppLayout from './components/AppLayout.js';
import { router } from './router/index.js';
import { auth, check } from './store/auth.js';

async function bootstrap() {
  // 已有 Token 时先向后端校验一次，失效则由守卫跳回登录页。
  if (auth.token) {
    await check();
  }
  createApp(AppLayout).use(router).mount('#app');
}

bootstrap();
