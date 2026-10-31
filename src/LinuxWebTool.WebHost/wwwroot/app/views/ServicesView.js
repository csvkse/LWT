import { computed, defineComponent, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

import EnvironmentBanner from '../components/services/EnvironmentBanner.js';
import SystemdSubmodule from '../components/services/SystemdSubmodule.js';
import DockerSubmodule from '../components/services/DockerSubmodule.js';
import SshSubmodule from '../components/services/SshSubmodule.js';
import SambaSubmodule from '../components/services/SambaSubmodule.js';
import NetworkFirewallSubmodule from '../components/services/NetworkFirewallSubmodule.js';
import ExtensionsSubmodule from '../components/services/ExtensionsSubmodule.js';

// 子模块元数据定义清单
const MODULE_CATALOG = [
  {
    group: '基础运行引擎',
    items: [
      { id: 'docker', name: 'Docker 容器服务', icon: '🐳', desc: '容器生命周期、日志与本地镜像' },
      { id: 'systemd', name: 'Systemd 系统服务', icon: '⚙️', desc: '系统服务单元控制与 journalctl 日志' },
    ],
  },
  {
    group: '系统与网络服务',
    items: [
      { id: 'ssh', name: 'OpenSSH 服务配置', icon: '🔒', desc: 'sshd 安全配置与 authorized_keys 维护' },
      { id: 'samba', name: 'Samba 共享管理', icon: '📁', desc: 'smb.conf 局域网文件夹共享与权限' },
      { id: 'network', name: '网络网卡与防火墙', icon: '🌐', desc: 'IP 拓扑、DNS 与 UFW 端口规则放行' },
    ],
  },
  {
    group: '动态扩展',
    items: [
      { id: 'extensions', name: '脚本扩展服务', icon: '🔌', desc: '基于契约脚本的动态自定义服务' },
    ],
  },
];

export default defineComponent({
  name: 'ServicesView',
  components: {
    EnvironmentBanner,
    SystemdSubmodule,
    DockerSubmodule,
    SshSubmodule,
    SambaSubmodule,
    NetworkFirewallSubmodule,
    ExtensionsSubmodule,
  },
  setup() {
    const route = useRoute();
    const router = useRouter();

    const envLoading = ref(false);
    const envInfo = ref({});
    const activeModuleId = ref('docker');

    // 扁平化列表用于移动端九宫格或快速查找
    const allModules = computed(() => {
      const res = [];
      for (const g of MODULE_CATALOG) {
        for (const item of g.items) {
          res.push(item);
        }
      }
      return res;
    });

    const activeModuleMeta = computed(() => {
      return allModules.value.find(m => m.id === activeModuleId.value) || allModules.value[0];
    });

    async function loadEnvironment() {
      envLoading.value = true;
      try {
        const res = await http(API.services.environment);
        if (res.ok && res.data) {
          envInfo.value = res.data;
          // 环境自适应默认模块激活：如果是纯 Linux 且没有 Docker，默认切到 systemd
          if (res.data.operatingSystem === 'Linux' && !res.data.hasDockerSocket && res.data.hasSystemd) {
            if (!route.query.module) {
              activeModuleId.value = 'systemd';
            }
          }
        }
      } catch (err) {
        toast.error('探测系统环境失败: ' + err.message);
      } finally {
        envLoading.value = false;
      }
    }

    function selectModule(moduleId) {
      activeModuleId.value = moduleId;
      router.replace({ path: '/services', query: { module: moduleId } });
    }

    watch(
      () => route.query.module,
      (newMod) => {
        if (typeof newMod === 'string' && newMod) {
          activeModuleId.value = newMod;
        }
      },
      { immediate: true }
    );

    onMounted(() => {
      loadEnvironment();
    });

    return {
      envLoading,
      envInfo,
      activeModuleId,
      activeModuleMeta,
      moduleCatalog: MODULE_CATALOG,
      allModules,
      loadEnvironment,
      selectModule,
    };
  },
  template: `
    <div class="space-y-4">
      <!-- 宿主全景环境画像栏 -->
      <EnvironmentBanner
        :env="envInfo"
        :loading="envLoading"
        @refresh="loadEnvironment()"
      />

      <!-- 服务中枢主交互区 (桌面端分栏，移动端平铺) -->
      <div class="flex flex-col lg:flex-row gap-4 items-start">
        <!-- 左侧二级子模块导航栏 (桌面端) -->
        <div class="hidden lg:flex flex-col gap-3 w-64 shrink-0 panel p-3">
          <div class="font-display font-semibold text-xs text-slate-400 uppercase tracking-wider pb-1 border-b border-cyber-line/50">
            服务中枢模块导航
          </div>

          <div v-for="grp in moduleCatalog" :key="grp.group" class="space-y-1">
            <div class="text-[11px] font-medium text-slate-500 px-2 pt-1">{{ grp.group }}</div>
            <button
              v-for="item in grp.items"
              :key="item.id"
              type="button"
              class="w-full text-left px-2.5 py-2 rounded text-xs flex items-center justify-between transition-colors group"
              :class="activeModuleId === item.id ? 'bg-cyan-500/15 border border-cyan-500/40 text-cyan-300 font-medium' : 'text-slate-300 hover:bg-slate-800/60 hover:text-white'"
              @click="selectModule(item.id)"
            >
              <div class="flex items-center gap-2 min-w-0">
                <span class="text-sm shrink-0">{{ item.icon }}</span>
                <span class="truncate">{{ item.name }}</span>
              </div>
              <span v-if="activeModuleId === item.id" class="w-1.5 h-1.5 rounded-full bg-cyan-400 shrink-0"></span>
            </button>
          </div>
        </div>

        <!-- 移动端顶部水平滚动胶囊菜单 -->
        <div class="lg:hidden flex items-center gap-1.5 overflow-x-auto w-full pb-1">
          <button
            v-for="item in allModules"
            :key="item.id"
            type="button"
            class="badge text-xs py-1.5 px-3 whitespace-nowrap shrink-0 transition-colors"
            :class="activeModuleId === item.id ? 'border-cyan-400 bg-cyan-500/20 text-cyan-300 font-medium' : 'border-slate-700 bg-slate-900/60 text-slate-400'"
            @click="selectModule(item.id)"
          >
            {{ item.icon }} {{ item.name }}
          </button>
        </div>

        <!-- 右侧子模块工作视窗 (Canvas) -->
        <div class="flex-1 w-full min-w-0">
          <!-- 子模块头部标题与描述 -->
          <div class="flex items-center justify-between mb-3">
            <div class="flex items-center gap-2">
              <span class="text-xl">{{ activeModuleMeta.icon }}</span>
              <div>
                <h2 class="font-display font-semibold text-slate-100 text-sm tracking-wide">
                  {{ activeModuleMeta.name }}
                </h2>
                <div class="text-xs text-slate-400">
                  {{ activeModuleMeta.desc }}
                </div>
              </div>
            </div>
          </div>

          <!-- 动态装配各子模块组件 -->
          <DockerSubmodule
            v-if="activeModuleId === 'docker'"
            :env="envInfo"
          />

          <SystemdSubmodule
            v-else-if="activeModuleId === 'systemd'"
            :env="envInfo"
          />

          <SshSubmodule
            v-else-if="activeModuleId === 'ssh'"
          />

          <SambaSubmodule
            v-else-if="activeModuleId === 'samba'"
          />

          <NetworkFirewallSubmodule
            v-else-if="activeModuleId === 'network'"
          />

          <ExtensionsSubmodule
            v-else-if="activeModuleId === 'extensions'"
            :env="envInfo"
          />
        </div>
      </div>
    </div>
  `,
});
