import { defineComponent, onMounted, reactive, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'NetworkFirewallSubmodule',
  setup() {
    const loading = ref(false);
    const nics = ref([]);
    const dns = ref({ nameservers: [], searchDomains: [], sourceDescription: '' });
    const firewall = ref({ isInstalled: false, isActive: false, backendType: 'none', rules: [] });

    // 新增防火墙规则表单
    const ruleForm = reactive({
      port: 8080,
      protocol: 'tcp',
      action: 'allow',
    });

    async function loadData() {
      loading.value = true;
      try {
        const [netRes, dnsRes, fwRes] = await Promise.all([
          http(API.services.networkConfig),
          http(API.services.dnsConfig),
          http(API.services.firewallConfig),
        ]);
        if (netRes.ok && netRes.data) nics.value = netRes.data;
        if (dnsRes.ok && dnsRes.data) dns.value = dnsRes.data;
        if (fwRes.ok && fwRes.data) firewall.value = fwRes.data;
      } catch (err) {
        toast.error('拉取网络与安全数据失败: ' + err.message);
      } finally {
        loading.value = false;
      }
    }

    async function addFirewallRule() {
      if (!ruleForm.port || ruleForm.port <= 0 || ruleForm.port > 65535) {
        toast.warning('请输入有效的端口号 (1 ~ 65535)');
        return;
      }
      try {
        const res = await http(API.services.firewallRule, {
          method: 'POST',
          body: {
            port: Number(ruleForm.port),
            protocol: ruleForm.protocol,
            action: 'allow',
          },
        });
        if (res.ok && res.data?.success) {
          toast.success(`端口 ${ruleForm.port}/${ruleForm.protocol} 已成功放行`);
          await loadData();
        } else {
          toast.error(res.data?.message || res.message || '放行端口失败');
        }
      } catch (err) {
        toast.error('操作失败: ' + err.message);
      }
    }

    async function deleteFirewallRule(port, proto) {
      try {
        const res = await http(API.services.firewallRule, {
          method: 'POST',
          body: {
            port: Number(port),
            protocol: proto,
            action: 'delete',
          },
        });
        if (res.ok && res.data?.success) {
          toast.success(`规则已成功移除`);
          await loadData();
        } else {
          toast.error(res.data?.message || res.message || '移除规则失败');
        }
      } catch (err) {
        toast.error('操作失败: ' + err.message);
      }
    }

    onMounted(() => {
      loadData();
    });

    return {
      loading,
      nics,
      dns,
      firewall,
      ruleForm,
      loadData,
      addFirewallRule,
      deleteFirewallRule,
    };
  },
  template: `
    <div class="flex flex-col gap-3">
      <!-- 顶部状态栏 -->
      <div class="panel p-3.5 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-2.5">
          <span class="text-xl">🌐</span>
          <div>
            <div class="flex items-center gap-2">
              <span class="font-display font-semibold text-slate-100 text-sm">系统网络拓扑与安全防火墙</span>
              <span class="badge" :class="firewall.isActive ? 'border-emerald-500/40 text-emerald-300' : 'border-slate-700 text-slate-500'">
                防火墙: {{ firewall.backendType }} ({{ firewall.isActive ? '活跃' : '关闭' }})
              </span>
            </div>
            <div class="text-xs text-slate-400 mt-0.5">
              检测到 {{ nics.length }} 个网络接口 · DNS 来源: {{ dns.sourceDescription || '系统解析器' }}
            </div>
          </div>
        </div>
        <button type="button" class="btn btn-xs hover:border-cyan-500/40" :disabled="loading" @click="loadData()">
          🔄 刷新网络状态
        </button>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-3">
        <!-- 网卡与 DNS 列表 -->
        <div class="panel p-4 flex flex-col gap-3">
          <div class="font-display font-medium text-slate-200 text-xs border-b border-cyber-line/50 pb-2 flex items-center justify-between">
            <span>物理与虚拟网卡拓扑</span>
            <span class="text-[11px] text-slate-400">DNS: {{ dns.nameservers?.join(', ') || '127.0.0.53' }}</span>
          </div>

          <div class="space-y-2 overflow-y-auto max-h-[22rem]">
            <div
              v-for="nic in nics"
              :key="nic.name"
              class="bg-black/30 rounded p-2.5 border border-cyber-line/30 flex flex-col gap-1.5"
            >
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-1.5 font-mono text-xs font-semibold text-slate-200">
                  <span class="w-2 h-2 rounded-full shrink-0" :class="nic.isUp ? 'bg-emerald-400' : 'bg-slate-600'"></span>
                  <span>{{ nic.name }}</span>
                  <span v-if="nic.isLoopback" class="badge text-[10px] border-slate-700 text-slate-500">回环</span>
                </div>
                <span class="text-[11px] font-mono text-slate-500">MAC: {{ nic.macAddress || '无' }}</span>
              </div>
              <div class="text-xs font-mono text-cyan-300/80 space-y-0.5">
                <div v-for="ip in nic.ipv4Addresses" :key="ip">
                  IPv4: {{ ip }}
                </div>
                <div v-if="nic.ipv4Addresses?.length === 0" class="text-slate-500 text-[11px]">
                  未分配 IPv4 地址
                </div>
              </div>
              <div v-if="nic.gateway" class="text-[11px] text-slate-400">
                默认网关: <span class="font-mono text-slate-300">{{ nic.gateway }}</span>
              </div>
            </div>
          </div>
        </div>

        <!-- 防火墙端口放行与规则 -->
        <div class="panel p-4 flex flex-col gap-3">
          <div class="font-display font-medium text-slate-200 text-xs border-b border-cyber-line/50 pb-2 flex items-center justify-between">
            <span>防火墙端口放行规则 (UFW / Firewalld)</span>
            <span class="text-[11px] text-slate-400 font-mono">{{ firewall.rules?.length || 0 }} 条放行规则</span>
          </div>

          <!-- 新增放行表单 -->
          <div class="flex items-center gap-2">
            <input
              type="number"
              v-model.number="ruleForm.port"
              min="1"
              max="65535"
              placeholder="端口号"
              class="input text-xs w-28"
            />
            <select v-model="ruleForm.protocol" class="input text-xs w-24">
              <option value="tcp">TCP</option>
              <option value="udp">UDP</option>
            </select>
            <button type="button" class="btn btn-xs btn-primary shrink-0" @click="addFirewallRule()">
              ➕ 放行端口
            </button>
          </div>

          <!-- 规则列表 -->
          <div class="space-y-1.5 overflow-y-auto max-h-[18rem]">
            <div
              v-for="r in firewall.rules"
              :key="r.ruleId"
              class="bg-black/30 rounded p-2 text-xs font-mono border border-cyber-line/30 flex items-center justify-between"
            >
              <div class="flex items-center gap-2">
                <span class="badge border-emerald-500/40 text-emerald-300 text-[10px]">放行</span>
                <span class="text-slate-200 font-semibold">{{ r.port }}/{{ r.protocol }}</span>
                <span class="text-[11px] text-slate-500 truncate max-w-[10rem]">来源: {{ r.fromAddress || 'Anywhere' }}</span>
              </div>
              <button
                type="button"
                class="text-rose-400 hover:text-rose-300 text-[11px] px-1.5 py-0.5 rounded border border-rose-500/30 hover:bg-rose-500/10"
                @click="deleteFirewallRule(r.port, r.protocol)"
              >
                移除
              </button>
            </div>
            <div v-if="!firewall.rules || firewall.rules.length === 0" class="text-slate-500 text-xs text-center py-6">
              {{ firewall.isInstalled ? '暂无端口放行规则。' : '当前系统未检测到 UFW/Firewalld 防火墙工具。' }}
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
});
