import { computed, defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { auth, logout } from '../store/auth.js';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import ToastHost from './ToastHost.js';
import ModalHost from './ModalHost.js';

// 顶部导航逻辑分组（5 大核心入口）
const NAV_GROUPS = [
  {
    key: 'overview',
    label: '概览',
    path: '/',
    isDirect: true,
  },
  {
    key: 'ops',
    label: '运维控制',
    children: [
      { path: '/terminal', label: 'Web 终端', desc: '网页交互式 PTY 终端会话' },
      { path: '/commands', label: '指令管理', desc: 'Linux 常用脚本库与执行' },
      { path: '/schedules', label: '定时任务', desc: 'Quartz Cron 周期调度' },
      { path: '/system', label: '系统监控', desc: 'CPU / 内存 / 磁盘 / 进程' },
    ],
  },
  {
    key: 'storage',
    label: '存储与媒体',
    children: [
      { path: '/files', label: '文件管理', desc: '在线浏览、编辑与上传下载' },
      { path: '/mounts', label: '挂载管理', desc: 'SMB / WebDAV / Rclone 挂载' },
      { path: '/transcode', label: '媒体转码', desc: 'FFmpeg 硬件加速队列转码' },
    ],
  },
  {
    key: 'network',
    label: '网络与穿透',
    children: [
      { path: '/gateway', label: '家庭网关', desc: 'YARP 反代与网站/端口转发' },
      { path: '/frp', label: 'FRP 穿透', desc: 'ProxyByCF 隧道 / 多线路 / 302代拉' },
      { path: '/easytier', label: 'EasyTier 组网', desc: '去中心化 P2P 虚拟局域网 / 节点与路由' },
    ],
  },
  {
    key: 'security',
    label: '安全与审计',
    children: [
      { path: '/keys', label: 'API 密钥', desc: 'REST & MCP 细粒度权限凭据' },
      { path: '/history', label: '执行历史', desc: '指令运行记录与历史输出' },
      { path: '/logs', label: '系统日志', desc: '审计日志与系统服务日志' },
    ],
  },
];

export default defineComponent({
  name: 'AppLayout',
  components: { ToastHost, ModalHost },
  setup() {
    const route = useRoute();
    const router = useRouter();
    const isPublic = computed(() => Boolean(route.meta.public));

    // 下拉菜单与移动端展开状态
    const activeMenu = ref(null);
    const mobileMenuOpen = ref(false);

    // 修改凭据弹窗
    const showCredential = ref(false);
    const credSaving = ref(false);
    const credForm = reactive({ currentPassword: '', newUserName: '', newPassword: '', confirm: '' });

    function isGroupActive(group) {
      if (group.isDirect) return route.path === group.path;
      return group.children ? group.children.some(c => route.path === c.path) : false;
    }

    let closeTimer = null;

    function toggleMenu(key) {
      if (closeTimer) {
        clearTimeout(closeTimer);
        closeTimer = null;
      }
      activeMenu.value = activeMenu.value === key ? null : key;
    }

    function openDropdown(key) {
      if (closeTimer) {
        clearTimeout(closeTimer);
        closeTimer = null;
      }
      activeMenu.value = key;
    }

    function closeDropdown() {
      if (closeTimer) clearTimeout(closeTimer);
      closeTimer = setTimeout(() => {
        activeMenu.value = null;
        closeTimer = null;
      }, 200);
    }

    function closeDropdownImmediate() {
      if (closeTimer) {
        clearTimeout(closeTimer);
        closeTimer = null;
      }
      activeMenu.value = null;
    }

    function toggleMobileMenu() {
      mobileMenuOpen.value = !mobileMenuOpen.value;
    }

    function closeMobileMenu() {
      mobileMenuOpen.value = false;
    }

    function openCredential() {
      Object.assign(credForm, { currentPassword: '', newUserName: '', newPassword: '', confirm: '' });
      showCredential.value = true;
    }

    async function saveCredential() {
      if (!credForm.currentPassword) return toast.error('请输入当前密码');
      if (!credForm.newUserName && !credForm.newPassword) return toast.error('新用户名与新密码至少填写一项');
      if (credForm.newPassword && credForm.newPassword.length < 6) return toast.error('新密码至少 6 位');
      if (credForm.newPassword && credForm.newPassword !== credForm.confirm) return toast.error('两次输入的新密码不一致');
      credSaving.value = true;
      try {
        const result = await http(API.auth.changeCredential, {
          method: 'POST',
          body: {
            currentPassword: credForm.currentPassword,
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

    onMounted(() => {
      window.addEventListener('click', closeDropdownImmediate);
    });

    onUnmounted(() => {
      window.removeEventListener('click', closeDropdownImmediate);
      if (closeTimer) {
        clearTimeout(closeTimer);
        closeTimer = null;
      }
    });

    return {
      isPublic,
      auth,
      NAV_GROUPS,
      activeMenu,
      mobileMenuOpen,
      handleLogout,
      isActive,
      isGroupActive,
      toggleMenu,
      openDropdown,
      closeDropdown,
      closeDropdownImmediate,
      toggleMobileMenu,
      closeMobileMenu,
      showCredential,
      credSaving,
      credForm,
      openCredential,
      saveCredential,
    };
  },
  template: `
    <div class="min-h-screen flex flex-col max-w-full overflow-x-hidden">
      <header v-if="!isPublic" class="sticky top-0 z-40 border-b border-cyber-line bg-cyber-bg/90 backdrop-blur">
        <div class="max-w-7xl mx-auto px-3 sm:px-4 h-14 flex items-center justify-between gap-2 sm:gap-6">
          <!-- Logo 与标题 -->
          <router-link to="/" class="flex items-center gap-2.5 shrink-0 hover:opacity-90 transition-opacity">
            <div class="w-8 h-8 rounded-lg border border-neon/40 flex items-center justify-center font-display text-neon-soft text-xs shadow-sm">LWT</div>
            <span class="font-display text-sm tracking-widest text-slate-200 font-semibold">LinuxWebTool</span>
          </router-link>

          <!-- 桌面端逻辑分组导航 -->
          <nav class="hidden md:flex items-center gap-1.5 flex-1 min-w-0">
            <template v-for="group in NAV_GROUPS" :key="group.key">
              <!-- 单项常驻链接 (概览) -->
              <router-link
                v-if="group.isDirect"
                :to="group.path"
                class="nav-link shrink-0 whitespace-nowrap"
                :class="{ active: isActive(group.path) }"
              >{{ group.label }}</router-link>

              <!-- 逻辑分组下拉菜单 -->
              <div
                v-else
                class="relative shrink-0"
                @mouseenter="openDropdown(group.key)"
                @mouseleave="closeDropdown()"
              >
                <button
                  type="button"
                  class="nav-link flex items-center gap-1 shrink-0 whitespace-nowrap cursor-pointer select-none"
                  :class="{ active: isGroupActive(group) || activeMenu === group.key }"
                  @click.stop="toggleMenu(group.key)"
                >
                  <span>{{ group.label }}</span>
                  <span class="text-[9px] text-slate-400 transition-transform duration-200" :class="{ 'rotate-180': activeMenu === group.key }">▾</span>
                </button>

                <!-- 下拉浮层容器（通过 pt-1.5 消除间隙，形成无缝 hover 桥接） -->
                <div
                  v-if="activeMenu === group.key"
                  class="absolute top-full left-0 pt-1.5 z-50"
                  @mouseenter="openDropdown(group.key)"
                  @mouseleave="closeDropdown()"
                >
                  <div class="w-56 py-1.5 rounded-xl bg-slate-900/95 border border-slate-700/80 shadow-2xl backdrop-blur-md flex flex-col gap-0.5">
                    <router-link
                      v-for="sub in group.children"
                      :key="sub.path"
                      :to="sub.path"
                      class="flex flex-col px-3 py-2 rounded-lg mx-1 text-xs hover:bg-slate-800/80 transition-colors"
                      :class="isActive(sub.path) ? 'text-cyan-300 bg-cyan-950/40 border border-cyan-500/30' : 'text-slate-300'"
                      @click="closeDropdownImmediate()"
                    >
                      <div class="flex items-center justify-between font-medium">
                        <span>{{ sub.label }}</span>
                        <span v-if="isActive(sub.path)" class="w-1.5 h-1.5 rounded-full bg-cyan-400"></span>
                      </div>
                      <span class="text-[11px] text-slate-500 truncate mt-0.5">{{ sub.desc }}</span>
                    </router-link>
                  </div>
                </div>
              </div>
            </template>
          </nav>

          <!-- 右侧用户信息与操作栏 -->
          <div class="flex items-center gap-2 shrink-0 text-sm text-slate-400">
            <!-- 移动端汉堡切换按钮 -->
            <button class="md:hidden btn btn-xs" @click.stop="toggleMobileMenu()">
              {{ mobileMenuOpen ? '✕' : '☰ 菜单' }}
            </button>

            <span class="hidden lg:inline text-xs">
              <span class="text-emerald-400 mr-1">●</span>{{ auth.userName || 'admin' }}
            </span>
            <button class="btn btn-xs" title="修改用户名 / 密码" @click="openCredential()">⚙</button>
            <button class="btn btn-xs" @click="handleLogout()">退出</button>
          </div>
        </div>

        <!-- 移动端抽屉手风琴菜单 -->
        <div v-if="mobileMenuOpen" class="md:hidden border-t border-cyber-line bg-slate-950/95 p-3 flex flex-col gap-2 max-h-[80vh] overflow-y-auto">
          <template v-for="group in NAV_GROUPS" :key="'m-' + group.key">
            <router-link
              v-if="group.isDirect"
              :to="group.path"
              class="nav-link font-medium block"
              :class="{ active: isActive(group.path) }"
              @click="closeMobileMenu()"
            >{{ group.label }}</router-link>

            <div v-else class="flex flex-col gap-1 border-t border-slate-800/60 pt-2">
              <span class="text-xs font-semibold text-slate-400 px-2">{{ group.label }}</span>
              <router-link
                v-for="sub in group.children"
                :key="'m-' + sub.path"
                :to="sub.path"
                class="flex items-center justify-between px-3 py-1.5 rounded text-xs"
                :class="isActive(sub.path) ? 'text-cyan-300 bg-cyan-950/40 font-medium' : 'text-slate-300'"
                @click="closeMobileMenu()"
              >
                <span>{{ sub.label }}</span>
                <span class="text-[10px] text-slate-500">{{ sub.desc }}</span>
              </router-link>
            </div>
          </template>
        </div>
      </header>

      <main class="flex-1 w-full max-w-7xl mx-auto px-3 sm:px-4 py-4 sm:py-5 overflow-x-hidden">
        <router-view />
      </main>

      <footer class="text-center text-xs text-slate-600 py-3 px-3">
        LinuxWebTool · 个人 Linux 运维指令台 · 请勿暴露至公网
      </footer>

      <ToastHost />
      <ModalHost />

      <div v-if="showCredential" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">修改用户名 / 密码</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">当前密码 *</span>
              <input class="input" type="password" v-model="credForm.currentPassword" autocomplete="current-password" />
            </label>
            <p class="text-[11px] text-slate-600 leading-relaxed">忘记当前密码？查看程序启动日志或 data/admin.json 的 generatedPassword 字段；也可用环境变量 Admin__Password 直接覆盖。</p>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">新用户名（留空则不修改，当前：{{ auth.userName }}）</span>
              <input class="input" v-model="credForm.newUserName" autocomplete="off" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">新密码（留空则不修改，至少 6 位）</span>
              <input class="input" type="password" v-model="credForm.newPassword" autocomplete="new-password" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">确认新密码</span>
              <input class="input" type="password" v-model="credForm.confirm" autocomplete="new-password" @keyup.enter="saveCredential()" />
            </label>
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
