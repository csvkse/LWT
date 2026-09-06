import { computed, defineComponent, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { auth, logout } from '../store/auth.js';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import ToastHost from './ToastHost.js';
import ModalHost from './ModalHost.js';

const NAV_ITEMS = [
  { path: '/', label: '概览' },
  { path: '/commands', label: '指令' },
  { path: '/schedules', label: '定时任务' },
  { path: '/system', label: '系统状态' },
  { path: '/history', label: '执行历史' },
  { path: '/logs', label: '日志' },
];

export default defineComponent({
  name: 'AppLayout',
  components: { ToastHost, ModalHost },
  setup() {
    const route = useRoute();
    const router = useRouter();
    const isPublic = computed(() => Boolean(route.meta.public));

    // 修改凭据弹窗
    const showCredential = ref(false);
    const credSaving = ref(false);
    const credForm = reactive({ newUserName: '', newPassword: '', confirm: '' });

    function openCredential() {
      Object.assign(credForm, { newUserName: '', newPassword: '', confirm: '' });
      showCredential.value = true;
    }

    async function saveCredential() {
      if (!credForm.newUserName && !credForm.newPassword) return toast.error('新用户名与新口令至少填写一项');
      if (credForm.newPassword && credForm.newPassword.length < 6) return toast.error('新口令至少 6 位');
      if (credForm.newPassword && credForm.newPassword !== credForm.confirm) return toast.error('两次输入的新口令不一致');
      credSaving.value = true;
      try {
        const result = await http(API.auth.changeCredential, {
          method: 'POST',
          body: {
            newUserName: credForm.newUserName || null,
            newPassword: credForm.newPassword || null,
          },
        });
        if (result.ok) {
          toast.success('凭据已更新，请使用新凭据重新登录');
          showCredential.value = false;
          logout();
          router.push('/login');
        }
      } finally {
        credSaving.value = false;
      }
    }

    function handleLogout() {
      logout();
      router.push('/login');
    }

    function isActive(path) {
      return route.path === path;
    }

    return {
      isPublic, auth, NAV_ITEMS, handleLogout, isActive,
      showCredential, credSaving, credForm, openCredential, saveCredential,
    };
  },
  template: `
    <div class="min-h-screen flex flex-col">
      <header v-if="!isPublic" class="sticky top-0 z-40 border-b border-cyber-line bg-cyber-bg/85 backdrop-blur">
        <div class="max-w-7xl mx-auto px-4 h-14 flex items-center gap-6">
          <div class="flex items-center gap-2.5">
            <div class="w-8 h-8 rounded-lg border border-neon/40 flex items-center justify-center font-display text-neon-soft text-xs">LWT</div>
            <span class="font-display text-sm tracking-widest text-slate-200">LinuxWebTool</span>
          </div>
          <nav class="flex items-center gap-1 flex-1">
            <router-link
              v-for="item in NAV_ITEMS"
              :key="item.path"
              :to="item.path"
              class="nav-link"
              :class="{ active: isActive(item.path) }"
            >{{ item.label }}</router-link>
          </nav>
          <div class="flex items-center gap-3 text-sm text-slate-400">
            <span class="hidden sm:inline">
              <span class="text-emerald-400 mr-1">●</span>{{ auth.userName || 'admin' }}
            </span>
            <button class="btn btn-xs" title="修改用户名 / 口令" @click="openCredential()">⚙</button>
            <button class="btn btn-xs" @click="handleLogout()">退出</button>
          </div>
        </div>
      </header>

      <main class="flex-1 max-w-7xl w-full mx-auto px-4 py-5 w-full">
        <router-view />
      </main>

      <footer class="text-center text-xs text-slate-600 py-3">
        LinuxWebTool · 个人 Linux 运维指令台 · 请勿暴露至公网
      </footer>

      <ToastHost />
      <ModalHost />

      <div v-if="showCredential" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">修改用户名 / 口令</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">新用户名（留空则不修改，当前：{{ auth.userName }}）</span>
              <input class="input" v-model="credForm.newUserName" autocomplete="off" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">新口令（留空则不修改，至少 6 位）</span>
              <input class="input" type="password" v-model="credForm.newPassword" autocomplete="new-password" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">确认新口令</span>
              <input class="input" type="password" v-model="credForm.confirm" autocomplete="new-password" @keyup.enter="saveCredential()" />
            </label>
            <p class="text-[11px] text-slate-600 leading-relaxed">
              忘记当前口令？查看程序启动日志或 data\admin.json 的 generatedPassword 字段；也可用环境变量 Admin__Password 直接覆盖。
            </p>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showCredential = false">取消</button>
            <button class="btn btn-primary" :disabled="credSaving" @click="saveCredential()">{{ credSaving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
