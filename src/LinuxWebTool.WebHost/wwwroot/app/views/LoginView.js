import { defineComponent, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
import { login } from '../store/auth.js';

export default defineComponent({
  name: 'LoginView',
  setup() {
    const router = useRouter();
    const form = reactive({ userName: 'admin', password: '' });
    const error = ref('');
    const loading = ref(false);

    async function submit() {
      if (!form.userName || !form.password) {
        error.value = '请输入用户名与口令';
        return;
      }
      loading.value = true;
      error.value = '';
      const result = await login(form.userName, form.password);
      loading.value = false;
      if (result.ok) {
        router.push('/');
      } else {
        error.value = result.message || '登录失败';
      }
    }

    return { form, error, loading, submit };
  },
  template: `
    <div class="min-h-screen flex items-center justify-center p-4">
      <div class="panel w-full max-w-sm p-7">
        <div class="flex flex-col items-center gap-2 mb-7">
          <div class="w-12 h-12 rounded-xl border border-neon/40 flex items-center justify-center font-display text-neon-soft">LWT</div>
          <div class="font-display tracking-[0.25em] text-slate-200 text-sm mt-1">LinuxWebTool</div>
          <div class="text-xs text-slate-500">Linux 指令控制台 · 登录</div>
        </div>

        <form @submit.prevent="submit()" class="flex flex-col gap-3.5">
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">用户名</span>
            <input class="input" v-model="form.userName" autocomplete="username" />
          </label>
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">口令</span>
            <input class="input" type="password" v-model="form.password" autocomplete="current-password" placeholder="••••••••" />
          </label>
          <p v-if="error" class="text-xs text-rose-400">{{ error }}</p>
          <button class="btn btn-primary w-full mt-1" type="submit" :disabled="loading">
            {{ loading ? '登录中…' : '登 录' }}
          </button>
        </form>

        <p class="text-[11px] text-slate-600 mt-5 leading-relaxed">
          首次启动的口令由服务自动生成，见程序日志或 data/admin.json；也可在 appsettings.json 的 Admin:Password 配置。
        </p>
      </div>
    </div>
  `,
});
