import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { formatTime } from '../utils/format.js';

export default defineComponent({
  name: 'GatewayView',
  setup() {
    const currentTab = ref('websites'); // websites | routes | clusters | tcp
    const loading = ref(false);

    // 网站代理
    const websites = ref([]);
    const showWebsiteModal = ref(false);
    const isEditWebsite = ref(false);
    const currentWebsiteId = ref(null);
    const websiteForm = reactive({
      name: '',
      targetUrl: '',
      rewriteBody: true,
      rewriteCookie: true,
      isEnabled: true,
    });

    // L7 路由
    const routes = ref([]);
    const showRouteModal = ref(false);
    const isEditRoute = ref(false);
    const currentRouteId = ref(null);
    const routeForm = reactive({
      routeId: '',
      clusterId: '',
      matchPath: '',
      matchHosts: '',
      orderNum: 0,
      isEnabled: true,
      metadata: '',
    });

    // L7 集群
    const clusters = ref([]);
    const showClusterModal = ref(false);
    const isEditCluster = ref(false);
    const currentClusterId = ref(null);
    const clusterForm = reactive({
      clusterId: '',
      loadBalancingPolicy: 'RoundRobin',
      destinations: '[{"Address":"http://127.0.0.1:8080"}]',
    });

    // L4 端口转发
    const tcpRoutes = ref([]);
    const showTcpModal = ref(false);
    const isEditTcp = ref(false);
    const currentTcpId = ref(null);
    const tcpForm = reactive({
      name: '',
      protocol: 'TCP',
      listenPort: 10000,
      forwardHost: '127.0.0.1',
      forwardPort: 80,
      isEnabled: true,
    });

    async function loadWebsites() {
      const res = await http(API.gateway.websites, { method: 'GET' });
      if (res.ok) websites.value = res.data || [];
    }

    async function loadRoutes() {
      const res = await http(API.gateway.routes, { method: 'GET' });
      if (res.ok) routes.value = res.data || [];
    }

    async function loadClusters() {
      const res = await http(API.gateway.clusters, { method: 'GET' });
      if (res.ok) clusters.value = res.data || [];
    }

    async function loadTcpRoutes() {
      const res = await http(API.gateway.tcpRoutes, { method: 'GET' });
      if (res.ok) tcpRoutes.value = res.data || [];
    }

    async function loadCurrent() {
      loading.value = true;
      if (currentTab.value === 'websites') await loadWebsites();
      else if (currentTab.value === 'routes') { await loadRoutes(); await loadClusters(); }
      else if (currentTab.value === 'clusters') await loadClusters();
      else if (currentTab.value === 'tcp') await loadTcpRoutes();
      loading.value = false;
    }

    // === 网站代理操作 ===
    function openCreateWebsite() {
      isEditWebsite.value = false;
      currentWebsiteId.value = null;
      Object.assign(websiteForm, {
        name: '',
        targetUrl: 'http://192.168.1.1:80',
        rewriteBody: true,
        rewriteCookie: true,
        isEnabled: true,
      });
      showWebsiteModal.value = true;
    }

    function openEditWebsite(w) {
      isEditWebsite.value = true;
      currentWebsiteId.value = w.id;
      Object.assign(websiteForm, {
        name: w.name,
        targetUrl: w.targetUrl,
        rewriteBody: w.rewriteBody,
        rewriteCookie: w.rewriteCookie,
        isEnabled: w.isEnabled,
      });
      showWebsiteModal.value = true;
    }

    async function saveWebsite() {
      if (!websiteForm.name.trim()) return toast.error('请输入站点名称');
      if (!websiteForm.targetUrl.trim()) return toast.error('请输入目标地址');

      const body = {
        name: websiteForm.name.trim(),
        targetUrl: websiteForm.targetUrl.trim(),
        rewriteBody: websiteForm.rewriteBody,
        rewriteCookie: websiteForm.rewriteCookie,
        isEnabled: websiteForm.isEnabled,
      };

      const res = isEditWebsite.value
        ? await http(API.gateway.websiteItem(currentWebsiteId.value), { method: 'PUT', body })
        : await http(API.gateway.websites, { method: 'POST', body });

      if (res.ok) {
        toast.success(isEditWebsite.value ? '网站代理已更新' : '网站代理已添加');
        showWebsiteModal.value = false;
        loadWebsites();
      }
    }

    async function deleteWebsite(w) {
      if (!confirm(`确定删除网站代理 "${w.name}" 吗？`)) return;
      const res = await http(API.gateway.websiteItem(w.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        loadWebsites();
      }
    }

    // === L7 路由操作 ===
    function openCreateRoute() {
      isEditRoute.value = false;
      currentRouteId.value = null;
      Object.assign(routeForm, {
        routeId: 'route_' + Math.random().toString(36).substring(2, 7),
        clusterId: clusters.value[0]?.clusterId || 'default-cluster',
        matchPath: '/app-proxy/{**catch-all}',
        matchHosts: '',
        orderNum: 100,
        isEnabled: true,
        metadata: '{}',
      });
      showRouteModal.value = true;
    }

    function openEditRoute(r) {
      isEditRoute.value = true;
      currentRouteId.value = r.id;
      Object.assign(routeForm, {
        routeId: r.routeId,
        clusterId: r.clusterId,
        matchPath: r.matchPath,
        matchHosts: r.matchHosts || '',
        orderNum: r.orderNum,
        isEnabled: r.isEnabled,
        metadata: r.metadata || '',
      });
      showRouteModal.value = true;
    }

    async function saveRoute() {
      if (!routeForm.routeId.trim()) return toast.error('请输入路由ID');
      if (!routeForm.clusterId.trim()) return toast.error('请输入集群ID');
      if (!routeForm.matchPath.trim()) return toast.error('请输入匹配路径');

      const body = {
        routeId: routeForm.routeId.trim(),
        clusterId: routeForm.clusterId.trim(),
        matchPath: routeForm.matchPath.trim(),
        matchHosts: routeForm.matchHosts.trim() || null,
        orderNum: Number(routeForm.orderNum) || 0,
        isEnabled: routeForm.isEnabled,
        metadata: routeForm.metadata.trim() || null,
      };

      const res = isEditRoute.value
        ? await http(API.gateway.routeItem(currentRouteId.value), { method: 'PUT', body })
        : await http(API.gateway.routes, { method: 'POST', body });

      if (res.ok) {
        toast.success('路由已保存');
        showRouteModal.value = false;
        loadRoutes();
      }
    }

    async function deleteRoute(r) {
      if (!confirm(`确定删除路由 "${r.routeId}" 吗？`)) return;
      const res = await http(API.gateway.routeItem(r.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        loadRoutes();
      }
    }

    // === L7 集群操作 ===
    function openCreateCluster() {
      isEditCluster.value = false;
      currentClusterId.value = null;
      Object.assign(clusterForm, {
        clusterId: 'cluster_' + Math.random().toString(36).substring(2, 7),
        loadBalancingPolicy: 'RoundRobin',
        destinations: '[{"Address":"http://192.168.1.100:8080"}]',
      });
      showClusterModal.value = true;
    }

    function openEditCluster(c) {
      isEditCluster.value = true;
      currentClusterId.value = c.id;
      Object.assign(clusterForm, {
        clusterId: c.clusterId,
        loadBalancingPolicy: c.loadBalancingPolicy,
        destinations: c.destinations,
      });
      showClusterModal.value = true;
    }

    async function saveCluster() {
      if (!clusterForm.clusterId.trim()) return toast.error('请输入集群ID');

      const body = {
        clusterId: clusterForm.clusterId.trim(),
        loadBalancingPolicy: clusterForm.loadBalancingPolicy,
        destinations: clusterForm.destinations.trim(),
        healthCheckConfig: null,
      };

      const res = isEditCluster.value
        ? await http(API.gateway.clusterItem(currentClusterId.value), { method: 'PUT', body })
        : await http(API.gateway.clusters, { method: 'POST', body });

      if (res.ok) {
        toast.success('集群已保存');
        showClusterModal.value = false;
        loadClusters();
      }
    }

    async function deleteCluster(c) {
      if (!confirm(`确定删除集群 "${c.clusterId}" 吗？`)) return;
      const res = await http(API.gateway.clusterItem(c.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        loadClusters();
      }
    }

    // === L4 端口转发操作 ===
    function openCreateTcp() {
      isEditTcp.value = false;
      currentTcpId.value = null;
      Object.assign(tcpForm, {
        name: '',
        protocol: 'TCP',
        listenPort: 10000,
        forwardHost: '192.168.1.100',
        forwardPort: 80,
        isEnabled: true,
      });
      showTcpModal.value = true;
    }

    function openEditTcp(t) {
      isEditTcp.value = true;
      currentTcpId.value = t.id;
      Object.assign(tcpForm, {
        name: t.name,
        protocol: t.protocol,
        listenPort: t.listenPort,
        forwardHost: t.forwardHost,
        forwardPort: t.forwardPort,
        isEnabled: t.isEnabled,
      });
      showTcpModal.value = true;
    }

    async function saveTcp() {
      if (!tcpForm.name.trim()) return toast.error('请输入规则名称');
      if (!tcpForm.listenPort || !tcpForm.forwardPort) return toast.error('端口无效');
      if (!tcpForm.forwardHost.trim()) return toast.error('请输入目标地址');

      const body = {
        name: tcpForm.name.trim(),
        protocol: tcpForm.protocol,
        listenPort: Number(tcpForm.listenPort),
        forwardHost: tcpForm.forwardHost.trim(),
        forwardPort: Number(tcpForm.forwardPort),
        isEnabled: tcpForm.isEnabled,
      };

      const res = isEditTcp.value
        ? await http(API.gateway.tcpRouteItem(currentTcpId.value), { method: 'PUT', body })
        : await http(API.gateway.tcpRoutes, { method: 'POST', body });

      if (res.ok) {
        toast.success('转发规则已保存');
        showTcpModal.value = false;
        loadTcpRoutes();
      }
    }

    async function deleteTcp(t) {
      if (!confirm(`确定删除端口转发规则 "${t.name}" 吗？`)) return;
      const res = await http(API.gateway.tcpRouteItem(t.id), { method: 'DELETE' });
      if (res.ok) {
        toast.success('已删除');
        loadTcpRoutes();
      }
    }

    function buildProxyLink(targetUrl) {
      try {
        const u = new URL(targetUrl.includes('://') ? targetUrl : 'https://' + targetUrl);
        const authority = u.port ? `${u.hostname}:${u.port}` : u.hostname;
        return `${window.location.origin}/proxy/${authority}/`;
      } catch {
        return '#';
      }
    }

    onMounted(loadCurrent);

    return {
      currentTab,
      loading,
      websites,
      showWebsiteModal,
      isEditWebsite,
      websiteForm,
      routes,
      showRouteModal,
      isEditRoute,
      routeForm,
      clusters,
      showClusterModal,
      isEditCluster,
      clusterForm,
      tcpRoutes,
      showTcpModal,
      isEditTcp,
      tcpForm,
      loadCurrent,
      openCreateWebsite,
      openEditWebsite,
      saveWebsite,
      deleteWebsite,
      openCreateRoute,
      openEditRoute,
      saveRoute,
      deleteRoute,
      openCreateCluster,
      openEditCluster,
      saveCluster,
      deleteCluster,
      openCreateTcp,
      openEditTcp,
      saveTcp,
      deleteTcp,
      buildProxyLink,
      formatTime,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold text-slate-100 flex items-center gap-2">
            家庭智能网关
            <span class="badge border-cyan-500/40 text-cyan-400 bg-cyan-950/30">借鉴 ProxyYARP</span>
          </h1>
          <p class="text-xs text-slate-400 mt-1">
            集成微软官方 YARP 2.3 高性能反向代理，支持即席网站代理（含 HTML/CSS 与 Cookie 隔离改写）以及 L4 TCP/UDP 端口转发。
          </p>
        </div>
        <div class="flex items-center gap-2">
          <button v-if="currentTab === 'websites'" class="btn btn-primary" @click="openCreateWebsite()">
            <span>+ 登记网站代理</span>
          </button>
          <button v-else-if="currentTab === 'routes'" class="btn btn-primary" @click="openCreateRoute()">
            <span>+ 新建 L7 路由</span>
          </button>
          <button v-else-if="currentTab === 'clusters'" class="btn btn-primary" @click="openCreateCluster()">
            <span>+ 新建集群</span>
          </button>
          <button v-else-if="currentTab === 'tcp'" class="btn btn-primary" @click="openCreateTcp()">
            <span>+ 新建端口转发</span>
          </button>
        </div>
      </div>

      <!-- Tab 切换 -->
      <div class="flex border-b border-slate-700/60 gap-1 text-xs font-medium">
        <button class="px-3.5 py-2 border-b-2 transition-colors cursor-pointer"
                :class="currentTab === 'websites' ? 'border-cyan-400 text-cyan-300 font-semibold' : 'border-transparent text-slate-400 hover:text-slate-200'"
                @click="currentTab = 'websites'; loadCurrent()">
          即席网站代理 (Websites)
        </button>
        <button class="px-3.5 py-2 border-b-2 transition-colors cursor-pointer"
                :class="currentTab === 'routes' ? 'border-cyan-400 text-cyan-300 font-semibold' : 'border-transparent text-slate-400 hover:text-slate-200'"
                @click="currentTab = 'routes'; loadCurrent()">
          L7 反向代理路由 (Routes)
        </button>
        <button class="px-3.5 py-2 border-b-2 transition-colors cursor-pointer"
                :class="currentTab === 'clusters' ? 'border-cyan-400 text-cyan-300 font-semibold' : 'border-transparent text-slate-400 hover:text-slate-200'"
                @click="currentTab = 'clusters'; loadCurrent()">
          上游集群 (Clusters)
        </button>
        <button class="px-3.5 py-2 border-b-2 transition-colors cursor-pointer"
                :class="currentTab === 'tcp' ? 'border-cyan-400 text-cyan-300 font-semibold' : 'border-transparent text-slate-400 hover:text-slate-200'"
                @click="currentTab = 'tcp'; loadCurrent()">
          L4 端口转发 (TCP/UDP)
        </button>
      </div>

      <!-- 1. 网站代理列表 -->
      <div v-if="currentTab === 'websites'" class="panel p-4 flex flex-col gap-3">
        <div v-if="loading && websites.length === 0" class="text-sm text-slate-400 py-8 text-center">加载中...</div>
        <div v-else-if="websites.length === 0" class="text-sm text-slate-400 py-12 text-center">
          暂未登记任何网站代理，点击右上角登记内网设备 Web 界面（如路由器、NAS、PVE、打印机等）。
        </div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-xs border-collapse">
            <thead>
              <tr class="border-b border-slate-700/50 text-slate-400 font-medium">
                <th class="py-2.5 px-3">名称</th>
                <th class="py-2.5 px-3">内网目标地址</th>
                <th class="py-2.5 px-3">网关直通入口</th>
                <th class="py-2.5 px-3">改写策略</th>
                <th class="py-2.5 px-3">状态</th>
                <th class="py-2.5 px-3 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/40">
              <tr v-for="w in websites" :key="w.id" class="hover:bg-slate-800/20 transition-colors">
                <td class="py-3 px-3 font-medium text-slate-200">{{ w.name }}</td>
                <td class="py-3 px-3">
                  <code class="px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 font-mono text-slate-300">
                    {{ w.targetUrl }}
                  </code>
                </td>
                <td class="py-3 px-3">
                  <a :href="buildProxyLink(w.targetUrl)" target="_blank"
                     class="text-cyan-400 hover:underline font-mono inline-flex items-center gap-1">
                    {{ buildProxyLink(w.targetUrl) }} ↗
                  </a>
                </td>
                <td class="py-3 px-3">
                  <div class="flex gap-1">
                    <span v-if="w.rewriteBody" class="badge border-cyan-500/40 text-cyan-400 bg-cyan-950/20">HTML改写</span>
                    <span v-if="w.rewriteCookie" class="badge border-purple-500/40 text-purple-400 bg-purple-950/20">Cookie隔离</span>
                  </div>
                </td>
                <td class="py-3 px-3">
                  <span class="badge" :class="w.isEnabled ? 'border-emerald-500/40 text-emerald-300 bg-emerald-950/30' : 'border-slate-600 text-slate-400 bg-slate-900'">
                    {{ w.isEnabled ? '● 已启用' : '○ 已停用' }}
                  </span>
                </td>
                <td class="py-3 px-3 text-right">
                  <div class="flex items-center justify-end gap-1.5">
                    <a :href="buildProxyLink(w.targetUrl)" target="_blank" class="btn btn-xs btn-primary">访问</a>
                    <button class="btn btn-xs" @click="openEditWebsite(w)">编辑</button>
                    <button class="btn btn-xs btn-danger" @click="deleteWebsite(w)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 2. L7 路由列表 -->
      <div v-if="currentTab === 'routes'" class="panel p-4 flex flex-col gap-3">
        <div v-if="loading && routes.length === 0" class="text-sm text-slate-400 py-8 text-center">加载中...</div>
        <div v-else-if="routes.length === 0" class="text-sm text-slate-400 py-12 text-center">
          暂无自定义 L7 路由规则。
        </div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-xs border-collapse">
            <thead>
              <tr class="border-b border-slate-700/50 text-slate-400 font-medium">
                <th class="py-2.5 px-3">路由 ID</th>
                <th class="py-2.5 px-3">匹配路径</th>
                <th class="py-2.5 px-3">匹配域名 (Hosts)</th>
                <th class="py-2.5 px-3">目标集群 ID</th>
                <th class="py-2.5 px-3">优先级 (Order)</th>
                <th class="py-2.5 px-3">状态</th>
                <th class="py-2.5 px-3 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/40">
              <tr v-for="r in routes" :key="r.id" class="hover:bg-slate-800/20 transition-colors">
                <td class="py-3 px-3 font-mono text-cyan-300">{{ r.routeId }}</td>
                <td class="py-3 px-3 font-mono text-slate-300">{{ r.matchPath }}</td>
                <td class="py-3 px-3 text-slate-400">{{ r.matchHosts || '*' }}</td>
                <td class="py-3 px-3 font-mono text-violet-300">{{ r.clusterId }}</td>
                <td class="py-3 px-3 text-slate-400">{{ r.orderNum }}</td>
                <td class="py-3 px-3">
                  <span class="badge" :class="r.isEnabled ? 'border-emerald-500/40 text-emerald-300 bg-emerald-950/30' : 'border-slate-600 text-slate-400 bg-slate-900'">
                    {{ r.isEnabled ? '● 启用' : '○ 停用' }}
                  </span>
                </td>
                <td class="py-3 px-3 text-right">
                  <div class="flex items-center justify-end gap-1.5">
                    <button class="btn btn-xs" @click="openEditRoute(r)">编辑</button>
                    <button class="btn btn-xs btn-danger" @click="deleteRoute(r)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 3. 上游集群列表 -->
      <div v-if="currentTab === 'clusters'" class="panel p-4 flex flex-col gap-3">
        <div v-if="loading && clusters.length === 0" class="text-sm text-slate-400 py-8 text-center">加载中...</div>
        <div v-else-if="clusters.length === 0" class="text-sm text-slate-400 py-12 text-center">
          暂无集群配置。
        </div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-xs border-collapse">
            <thead>
              <tr class="border-b border-slate-700/50 text-slate-400 font-medium">
                <th class="py-2.5 px-3">集群 ID</th>
                <th class="py-2.5 px-3">负载均衡策略</th>
                <th class="py-2.5 px-3">目标节点 (Destinations)</th>
                <th class="py-2.5 px-3">更新时间</th>
                <th class="py-2.5 px-3 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/40">
              <tr v-for="c in clusters" :key="c.id" class="hover:bg-slate-800/20 transition-colors">
                <td class="py-3 px-3 font-mono text-violet-300">{{ c.clusterId }}</td>
                <td class="py-3 px-3 text-slate-300">{{ c.loadBalancingPolicy }}</td>
                <td class="py-3 px-3 font-mono text-slate-400 break-all">{{ c.destinations }}</td>
                <td class="py-3 px-3 text-slate-500">{{ formatTime(c.updateTime) }}</td>
                <td class="py-3 px-3 text-right">
                  <div class="flex items-center justify-end gap-1.5">
                    <button class="btn btn-xs" @click="openEditCluster(c)">编辑</button>
                    <button class="btn btn-xs btn-danger" @click="deleteCluster(c)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 4. L4 端口转发列表 -->
      <div v-if="currentTab === 'tcp'" class="panel p-4 flex flex-col gap-3">
        <div v-if="loading && tcpRoutes.length === 0" class="text-sm text-slate-400 py-8 text-center">加载中...</div>
        <div v-else-if="tcpRoutes.length === 0" class="text-sm text-slate-400 py-12 text-center">
          暂无端口转发规则。
        </div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-xs border-collapse">
            <thead>
              <tr class="border-b border-slate-700/50 text-slate-400 font-medium">
                <th class="py-2.5 px-3">规则名称</th>
                <th class="py-2.5 px-3">协议</th>
                <th class="py-2.5 px-3">监听端口</th>
                <th class="py-2.5 px-3">转发目标</th>
                <th class="py-2.5 px-3">状态</th>
                <th class="py-2.5 px-3 text-right">操作</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/40">
              <tr v-for="t in tcpRoutes" :key="t.id" class="hover:bg-slate-800/20 transition-colors">
                <td class="py-3 px-3 font-medium text-slate-200">{{ t.name }}</td>
                <td class="py-3 px-3">
                  <span class="badge" :class="t.protocol === 'UDP' ? 'border-amber-500/50 text-amber-300' : 'border-cyan-500/50 text-cyan-300'">
                    {{ t.protocol }}
                  </span>
                </td>
                <td class="py-3 px-3 font-mono text-cyan-300">:{{ t.listenPort }}</td>
                <td class="py-3 px-3 font-mono text-slate-300">{{ t.forwardHost }}:{{ t.forwardPort }}</td>
                <td class="py-3 px-3">
                  <span class="badge" :class="t.isEnabled ? 'border-emerald-500/40 text-emerald-300 bg-emerald-950/30' : 'border-slate-600 text-slate-400 bg-slate-900'">
                    {{ t.isEnabled ? '● 监听中' : '○ 已停止' }}
                  </span>
                </td>
                <td class="py-3 px-3 text-right">
                  <div class="flex items-center justify-end gap-1.5">
                    <button class="btn btn-xs" @click="openEditTcp(t)">编辑</button>
                    <button class="btn btn-xs btn-danger" @click="deleteTcp(t)">删除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 模态框：网站代理 -->
      <div v-if="showWebsiteModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-md p-5 flex flex-col gap-4">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEditWebsite ? '编辑网站代理' : '登记网站代理' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showWebsiteModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <label class="block text-slate-400 mb-1">站点名称 <span class="text-rose-400">*</span></label>
              <input v-model="websiteForm.name" class="input" placeholder="例如：OpenWrt主路由 / 群晖NAS" />
            </div>

            <div>
              <label class="block text-slate-400 mb-1">内网目标地址 (含端口) <span class="text-rose-400">*</span></label>
              <input v-model="websiteForm.targetUrl" class="input font-mono" placeholder="http://192.168.1.1:80" />
            </div>

            <div class="grid grid-cols-2 gap-2 pt-2">
              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="websiteForm.rewriteBody" class="accent-cyan-500" />
                <span class="text-slate-200">改写HTML绝对路径</span>
              </label>
              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="websiteForm.rewriteCookie" class="accent-cyan-500" />
                <span class="text-slate-200">隔离Cookie作用域</span>
              </label>
            </div>

            <div>
              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="websiteForm.isEnabled" class="accent-emerald-500" />
                <span class="text-slate-200">立即启用该站点代理</span>
              </label>
            </div>
          </div>

          <div class="flex justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showWebsiteModal = false">取消</button>
            <button class="btn btn-primary" @click="saveWebsite()">保存</button>
          </div>
        </div>
      </div>

      <!-- 模态框：L7 路由 -->
      <div v-if="showRouteModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEditRoute ? '编辑 L7 路由' : '新建 L7 路由' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showRouteModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-400 mb-1">路由 ID <span class="text-rose-400">*</span></label>
                <input v-model="routeForm.routeId" class="input font-mono" placeholder="my-api-route" />
              </div>
              <div>
                <label class="block text-slate-400 mb-1">目标集群 ID <span class="text-rose-400">*</span></label>
                <input v-model="routeForm.clusterId" class="input font-mono" placeholder="my-cluster" />
              </div>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">匹配路径 (Route Match Path) <span class="text-rose-400">*</span></label>
              <input v-model="routeForm.matchPath" class="input font-mono" placeholder="/service/{**catch-all}" />
            </div>

            <div>
              <label class="block text-slate-400 mb-1">匹配域名 (Hosts，英文逗号分隔，可选)</label>
              <input v-model="routeForm.matchHosts" class="input font-mono" placeholder="api.local, myhost.lan" />
            </div>

            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-400 mb-1">排序优先级 (OrderNum)</label>
                <input v-model.number="routeForm.orderNum" type="number" class="input font-mono" />
              </div>
              <div class="flex flex-col justify-end">
                <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                  <input type="checkbox" v-model="routeForm.isEnabled" class="accent-emerald-500" />
                  <span class="text-slate-200">启用该路由</span>
                </label>
              </div>
            </div>
          </div>

          <div class="flex justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showRouteModal = false">取消</button>
            <button class="btn btn-primary" @click="saveRoute()">保存</button>
          </div>
        </div>
      </div>

      <!-- 模态框：集群 -->
      <div v-if="showClusterModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 flex flex-col gap-4">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEditCluster ? '编辑集群' : '新建集群' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showClusterModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <label class="block text-slate-400 mb-1">集群 ID <span class="text-rose-400">*</span></label>
              <input v-model="clusterForm.clusterId" class="input font-mono" placeholder="my-cluster" />
            </div>

            <div>
              <label class="block text-slate-400 mb-1">负载均衡算法</label>
              <select v-model="clusterForm.loadBalancingPolicy" class="input">
                <option value="RoundRobin">RoundRobin (轮询)</option>
                <option value="Random">Random (随机)</option>
                <option value="LeastRequests">LeastRequests (最少连接)</option>
                <option value="PowerOfTwoChoices">PowerOfTwoChoices (两选一最优)</option>
              </select>
            </div>

            <div>
              <label class="block text-slate-400 mb-1">目标节点列表 (Destinations JSON) <span class="text-rose-400">*</span></label>
              <textarea v-model="clusterForm.destinations" rows="4" class="input font-mono text-[11px]" placeholder='[{"Address":"http://192.168.1.100:8080"}]'></textarea>
            </div>
          </div>

          <div class="flex justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showClusterModal = false">取消</button>
            <button class="btn btn-primary" @click="saveCluster()">保存</button>
          </div>
        </div>
      </div>

      <!-- 模态框：L4 端口转发 -->
      <div v-if="showTcpModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-md p-5 flex flex-col gap-4">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base">
              {{ isEditTcp ? '编辑端口转发' : '新建端口转发' }}
            </h3>
            <button class="text-slate-400 hover:text-slate-200" @click="showTcpModal = false">✕</button>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div>
              <label class="block text-slate-400 mb-1">规则名称 <span class="text-rose-400">*</span></label>
              <input v-model="tcpForm.name" class="input" placeholder="例如：SSH中转 / Minecraft服务器" />
            </div>

            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-400 mb-1">传输协议</label>
                <select v-model="tcpForm.protocol" class="input">
                  <option value="TCP">TCP</option>
                  <option value="UDP">UDP</option>
                </select>
              </div>
              <div>
                <label class="block text-slate-400 mb-1">本地监听端口 <span class="text-rose-400">*</span></label>
                <input v-model.number="tcpForm.listenPort" type="number" min="1" max="65535" class="input font-mono" />
              </div>
            </div>

            <div class="grid grid-cols-3 gap-3">
              <div class="col-span-2">
                <label class="block text-slate-400 mb-1">转发目标 IP / 域名 <span class="text-rose-400">*</span></label>
                <input v-model="tcpForm.forwardHost" class="input font-mono" placeholder="192.168.1.50" />
              </div>
              <div>
                <label class="block text-slate-400 mb-1">目标端口 <span class="text-rose-400">*</span></label>
                <input v-model.number="tcpForm.forwardPort" type="number" min="1" max="65535" class="input font-mono" />
              </div>
            </div>

            <div class="pt-1">
              <label class="flex items-center gap-2 p-2 rounded bg-slate-900/60 border border-slate-800 cursor-pointer">
                <input type="checkbox" v-model="tcpForm.isEnabled" class="accent-emerald-500" />
                <span class="text-slate-200">立即开启转发监听</span>
              </label>
            </div>
          </div>

          <div class="flex justify-end gap-2 border-t border-slate-700/60 pt-3">
            <button class="btn" @click="showTcpModal = false">取消</button>
            <button class="btn btn-primary" @click="saveTcp()">保存</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
