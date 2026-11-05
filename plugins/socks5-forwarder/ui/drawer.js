// drawer.js - 统一 Slide-over 侧滑抽屉控制器 (取代所有模态框)

import { store, apiCall, refreshAllData, eventBus } from './store.js';
import { VisualRuleBuilder } from './rule_builder.js';

export class ModelessDrawer {
  constructor() {
    this.drawerEl = document.getElementById('slideoverDrawer');
    this.backdropEl = document.getElementById('slideoverBackdrop');
    this.titleEl = document.getElementById('drawerTitle');
    this.iconEl = document.getElementById('drawerIcon');
    this.bodyEl = document.getElementById('drawerBody');
    this.closeBtn = document.getElementById('drawerCloseBtn');

    this.isOpen = false;
    this.currentType = null;
    this.ruleBuilderInstance = null;
    this.isDirty = false;

    this.initEvents();
  }

  initEvents() {
    if (this.closeBtn) {
      this.closeBtn.addEventListener('click', () => this.close());
    }
    if (this.backdropEl) {
      this.backdropEl.addEventListener('click', () => this.close());
    }
    window.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && this.isOpen) {
        this.close();
      }
    });
  }

  open(title, icon, type) {
    this.titleEl.textContent = title;
    this.iconEl.textContent = icon;
    this.currentType = type;
    this.bodyEl.innerHTML = '';
    this.isDirty = false;

    this.backdropEl.classList.remove('hidden');
    // 强制触发 reflow 产生平滑过渡
    void this.drawerEl.offsetWidth;
    this.drawerEl.classList.remove('translate-x-full');
    this.isOpen = true;
  }

  close() {
    if (!this.isOpen) return;
    if (this.isDirty && !confirm('当前抽屉中有未保存的修改，确定退出吗？')) {
      return;
    }
    this.drawerEl.classList.add('translate-x-full');
    setTimeout(() => {
      this.backdropEl.classList.add('hidden');
      this.bodyEl.innerHTML = '';
      this.isOpen = false;
      this.currentType = null;
      this.ruleBuilderInstance = null;
      this.isDirty = false;
    }, 280);
  }

  // 1. 抽屉: 编辑/新建上游节点
  openUpstream(node = null) {
    const isEdit = !!(node && node.id);
    this.open(isEdit ? '编辑远端出口服务节点' : '新增远端出口节点', '📡', 'upstream');

    const form = document.createElement('form');
    form.className = 'space-y-4 text-xs';
    form.innerHTML = `
      <input type="hidden" id="drUpId" value="${node?.id || ''}">
      <div class="grid grid-cols-2 gap-3">
        <div>
          <label class="block text-slate-400 mb-1">节点名称</label>
          <input type="text" id="drUpName" required value="${node?.name || ''}" placeholder="如: 日本优化节点" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">出站协议类型</label>
          <select id="drUpType" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-cyan-300 font-bold focus:outline-none focus:border-cyan-500">
            <option value="vless" ${node?.type === 'vless' ? 'selected' : ''}>VLESS (XTLS-Vision / TLS)</option>
            <option value="trojan" ${node?.type === 'trojan' ? 'selected' : ''}>Trojan (TLS + SHA224)</option>
            <option value="shadowsocks" ${node?.type === 'shadowsocks' ? 'selected' : ''}>Shadowsocks (AEAD)</option>
            <option value="lwt_wss" ${node?.type === 'lwt_wss' ? 'selected' : ''}>LWT Ingress 伪装隧道 (WSS)</option>
            <option value="socks5" ${node?.type === 'socks5' ? 'selected' : ''}>标准 SOCKS5 代理</option>
            <option value="http" ${node?.type === 'http' ? 'selected' : ''}>HTTP CONNECT 代理</option>
          </select>
        </div>
      </div>

      <!-- 主机与端口 -->
      <div id="drBoxHostPort" class="grid grid-cols-3 gap-3">
        <div class="col-span-2">
          <label class="block text-slate-400 mb-1">服务器主机 / 域名</label>
          <input type="text" id="drUpServer" value="${node?.server || ''}" placeholder="proxy.example.com" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">端口</label>
          <input type="number" id="drUpPort" min="1" max="65535" value="${node?.port || 443}" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono focus:outline-none focus:border-cyan-500">
        </div>
      </div>

      <!-- WSS 专属输入区域 -->
      <div id="drBoxWss" class="space-y-2.5 ${node?.type === 'lwt_wss' ? '' : 'hidden'}">
        <div>
          <label class="block text-slate-400 mb-1">WSS 伪装隧道地址 (URL)</label>
          <input type="text" id="drUpUrl" value="${node?.url || ''}" placeholder="wss://node.example.com:5271/telemetry/metrics" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">Ingress 访问令牌 (Token)</label>
          <input type="password" id="drUpToken" value="${node?.token || ''}" placeholder="留空则无鉴权" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
        </div>
      </div>

      <!-- 凭据: UUID (VLESS) -->
      <div id="drBoxUuid" class="${node?.type === 'vless' ? '' : 'hidden'}">
        <label class="block text-slate-400 mb-1">用户 UUID</label>
        <input type="text" id="drUpUuid" value="${node?.uuid || ''}" placeholder="00000000-0000-0000-0000-000000000000" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
      </div>

      <!-- 凭据: 密码 (Trojan / Shadowsocks) -->
      <div id="drBoxSecret" class="${(node?.type === 'trojan' || node?.type === 'shadowsocks') ? '' : 'hidden'}">
        <label class="block text-slate-400 mb-1">连接密码 / 预共享密钥</label>
        <input type="password" id="drUpSecret" value="${node?.password || ''}" placeholder="密码" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
      </div>

      <!-- 流控与指纹 (VLESS) -->
      <div id="drBoxVision" class="grid grid-cols-2 gap-3 ${(node?.type === 'vless') ? '' : 'hidden'}">
        <div>
          <label class="block text-slate-400 mb-1">流控 (Flow)</label>
          <input type="text" id="drUpFlow" value="${node?.flow || 'xtls-rprx-vision'}" placeholder="xtls-rprx-vision" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">客户端指纹 (Fingerprint)</label>
          <input type="text" id="drUpFp" value="${node?.fingerprint || 'chrome'}" placeholder="chrome" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
        </div>
      </div>

      <!-- TLS 与 SNI -->
      <div class="grid grid-cols-2 gap-3 pt-2 border-t border-slate-800">
        <div>
          <label class="block text-slate-400 mb-1">TLS SNI (对齐证书)</label>
          <input type="text" id="drUpSni" value="${node?.sni || ''}" placeholder="留空则同主机名" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
        </div>
        <div class="flex items-center gap-2 pt-6">
          <input type="checkbox" id="drUpSkipTls" ${node?.skipTlsVerify ? 'checked' : ''} class="rounded bg-slate-800 border-slate-700 text-cyan-500 focus:ring-0">
          <label for="drUpSkipTls" class="text-slate-300">跳过 TLS 证书校验</label>
        </div>
      </div>

      <div class="flex justify-between items-center pt-4 border-t border-slate-800">
        ${isEdit ? `<button type="button" class="btn-del-node text-xs text-rose-400 hover:text-rose-300 transition">🗑 删除节点</button>` : '<div></div>'}
        <div class="flex gap-2">
          <button type="button" class="btn-cancel px-4 py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">取消</button>
          <button type="submit" class="px-5 py-2 bg-cyan-600 hover:bg-cyan-500 text-white font-medium rounded-lg shadow-lg shadow-cyan-600/30">保存节点</button>
        </div>
      </div>
    `;

    // 协议切换动态控制
    const selType = form.querySelector('#drUpType');
    selType.addEventListener('change', () => {
      const val = selType.value;
      form.querySelector('#drBoxWss').classList.toggle('hidden', val !== 'lwt_wss');
      form.querySelector('#drBoxHostPort').classList.toggle('hidden', val === 'lwt_wss');
      form.querySelector('#drBoxUuid').classList.toggle('hidden', val !== 'vless');
      form.querySelector('#drBoxVision').classList.toggle('hidden', val !== 'vless');
      form.querySelector('#drBoxSecret').classList.toggle('hidden', val !== 'trojan' && val !== 'shadowsocks');
    });

    form.querySelectorAll('input, select').forEach(el => el.addEventListener('change', () => { this.isDirty = true; }));
    form.querySelector('.btn-cancel').addEventListener('click', () => this.close());

    if (isEdit) {
      form.querySelector('.btn-del-node')?.addEventListener('click', async () => {
        if (!confirm(`确定删除节点 [${node.name}] 吗？`)) return;
        await apiCall(`upstreams?id=${encodeURIComponent(node.id)}`, { method: 'DELETE' });
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '节点已删除' });
      });
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const payload = {
        id: form.querySelector('#drUpId').value || undefined,
        name: form.querySelector('#drUpName').value.trim(),
        type: form.querySelector('#drUpType').value,
        server: form.querySelector('#drUpServer').value.trim(),
        port: parseInt(form.querySelector('#drUpPort').value) || 443,
        url: form.querySelector('#drUpUrl').value.trim(),
        token: form.querySelector('#drUpToken').value.trim(),
        uuid: form.querySelector('#drUpUuid').value.trim(),
        flow: form.querySelector('#drUpFlow').value.trim(),
        fingerprint: form.querySelector('#drUpFp').value.trim(),
        password: form.querySelector('#drUpSecret').value.trim(),
        sni: form.querySelector('#drUpSni').value.trim(),
        skipTlsVerify: form.querySelector('#drUpSkipTls').checked
      };

      try {
        await apiCall('upstreams', { method: 'POST', body: payload });
        this.isDirty = false;
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '节点已成功保存！' });
      } catch (err) {
        alert('保存节点失败: ' + err.message);
      }
    });

    this.bodyEl.appendChild(form);
  }

  // 2. 抽屉: 编辑/新建分流线路流水线
  openPipeline(pipe = null) {
    const isEdit = !!(pipe && pipe.id);
    this.open(isEdit ? '编排分流线路流水线' : '新建分流线路流水线', '🔀', 'pipeline');

    const form = document.createElement('form');
    form.className = 'space-y-4 text-xs';
    form.innerHTML = `
      <input type="hidden" id="drPipeId" value="${pipe?.id || ''}">
      <div>
        <label class="block text-slate-400 mb-1">线路流水线名称</label>
        <input type="text" id="drPipeName" required value="${pipe?.name || ''}" placeholder="如: AI 研发加速专线" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
      </div>
      <div>
        <label class="block text-slate-400 mb-1">线路说明 (可选)</label>
        <input type="text" id="drPipeDesc" value="${pipe?.description || ''}" placeholder="简要描述用途" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
      </div>

      <div class="grid grid-cols-2 gap-3">
        <div>
          <label class="block text-slate-400 mb-1">默认出站动作</label>
          <select id="drPipeDefaultAction" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
            <option value="proxy" ${pipe?.defaultAction === 'proxy' ? 'selected' : ''}>Proxy (走代理)</option>
            <option value="direct" ${pipe?.defaultAction === 'direct' ? 'selected' : ''}>Direct (直连网络)</option>
            <option value="reject" ${pipe?.defaultAction === 'reject' ? 'selected' : ''}>Reject (阻断拦截)</option>
          </select>
        </div>
        <div>
          <label class="block text-slate-400 mb-1">默认落地出站 (节点 / 链式)</label>
          <select id="drPipeDefaultOutbound" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-cyan-300 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
            <option value="">-- (遵循默认拓扑) --</option>
            ${store.upstreams.map(u => `<option value="${u.id}" ${pipe?.defaultOutbound === u.id ? 'selected' : ''}>📡 [节点] ${u.name}</option>`).join('')}
            ${store.chains.map(c => `<option value="${c.id}" ${pipe?.defaultOutbound === c.id ? 'selected' : ''}>🔗 [链式] ${c.name}</option>`).join('')}
          </select>
        </div>
      </div>

      <div class="flex items-center justify-between p-2.5 rounded bg-slate-950/60 border border-slate-800">
        <span class="text-slate-300 font-bold">局域网私网直连短路 (RFC 1918)</span>
        <label class="flex items-center gap-1.5 cursor-pointer text-cyan-400 text-xs">
          <input type="checkbox" id="drPipeDirectPrivate" ${pipe?.directPrivateNetwork !== false ? 'checked' : ''} class="rounded bg-slate-800 border-slate-700 text-cyan-500 focus:ring-0">
          <span>开启短路保护</span>
        </label>
      </div>

      <!-- 可视化分流规则条目构建器 -->
      <div>
        <label class="block text-slate-400 mb-1 font-bold">分流决策规则列表</label>
        <div id="drRuleBuilderContainer"></div>
      </div>

      <div class="flex justify-between items-center pt-4 border-t border-slate-800">
        ${isEdit && pipe.id !== 'pipe-default' ? `<button type="button" class="btn-del-pipe text-xs text-rose-400 hover:text-rose-300 transition">🗑 删除线路</button>` : '<div></div>'}
        <div class="flex gap-2">
          <button type="button" class="btn-cancel px-4 py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">取消</button>
          <button type="submit" class="px-5 py-2 bg-indigo-600 hover:bg-indigo-500 text-white font-medium rounded-lg shadow-lg shadow-indigo-600/30">保存流水线</button>
        </div>
      </div>
    `;

    // 挂载可视化规则构建器
    const ruleBox = form.querySelector('#drRuleBuilderContainer');
    this.ruleBuilderInstance = new VisualRuleBuilder(ruleBox, pipe?.rules || []);

    form.querySelectorAll('input, select').forEach(el => el.addEventListener('change', () => { this.isDirty = true; }));
    form.querySelector('.btn-cancel').addEventListener('click', () => this.close());

    if (isEdit && pipe.id !== 'pipe-default') {
      form.querySelector('.btn-del-pipe')?.addEventListener('click', async () => {
        if (!confirm(`确定删除线路 [${pipe.name}] 吗？`)) return;
        await apiCall(`pipelines?id=${encodeURIComponent(pipe.id)}`, { method: 'DELETE' });
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '线路已删除' });
      });
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const rules = this.ruleBuilderInstance.getRules();
      const payload = {
        id: form.querySelector('#drPipeId').value || undefined,
        name: form.querySelector('#drPipeName').value.trim(),
        description: form.querySelector('#drPipeDesc').value.trim(),
        defaultAction: form.querySelector('#drPipeDefaultAction').value,
        defaultOutbound: form.querySelector('#drPipeDefaultOutbound').value,
        directPrivateNetwork: form.querySelector('#drPipeDirectPrivate').checked,
        rules: rules
      };

      try {
        await apiCall('pipelines', { method: 'POST', body: payload });
        this.isDirty = false;
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '分流流水线保存成功！' });
      } catch (err) {
        alert('保存线路失败: ' + err.message);
      }
    });

    this.bodyEl.appendChild(form);
  }

  // 3. 抽屉: 编辑/新建入站网关
  openInbound(inbound = null) {
    const isEdit = !!(inbound && inbound.id);
    this.open(isEdit ? '配置流量接入网关' : '新建流量接入端口', '📥', 'inbound');

    const form = document.createElement('form');
    form.className = 'space-y-4 text-xs';
    form.innerHTML = `
      <input type="hidden" id="drInId" value="${inbound?.id || ''}">
      <div class="grid grid-cols-2 gap-3">
        <div>
          <label class="block text-slate-400 mb-1">网关规则名称</label>
          <input type="text" id="drInName" required value="${inbound?.name || ''}" placeholder="如: 局域网混合接入" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">主监听端口 (Primary)</label>
          <input type="number" id="drInPort" required min="1024" max="65535" value="${inbound?.listenPort || 10801}" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono focus:outline-none focus:border-cyan-500">
        </div>
      </div>

      <div>
        <label class="block text-slate-400 mb-1">附加监听端口 (多端口共享策略，逗号分隔)</label>
        <input type="text" id="drInAdditional" value="${(inbound?.additionalPorts || []).join(', ')}" placeholder="如: 10802, 10803" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
      </div>

      <div class="grid grid-cols-2 gap-3">
        <div>
          <label class="block text-slate-400 mb-1">协议识别类型</label>
          <select id="drInProto" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
            <option value="mixed" ${inbound?.protocol === 'mixed' ? 'selected' : ''}>Mixed (SOCKS5 / HTTP 自适应)</option>
            <option value="socks5" ${inbound?.protocol === 'socks5' ? 'selected' : ''}>纯 SOCKS5 代理</option>
            <option value="http" ${inbound?.protocol === 'http' ? 'selected' : ''}>纯 HTTP CONNECT 代理</option>
          </select>
        </div>
        <div>
          <label class="block text-slate-400 mb-1">挂载的分流线路流水线</label>
          <select id="drInPipeId" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-indigo-300 font-bold focus:outline-none focus:border-indigo-500">
            <option value="">-- (暂不挂载，设为待命接入网关) --</option>
            ${store.pipelines.map(p => `<option value="${p.id}" ${inbound?.pipelineId === p.id ? 'selected' : ''}>🔀 ${p.name}</option>`).join('')}
          </select>
        </div>
      </div>

      <div>
        <label class="block text-slate-400 mb-1">客户端 IP 白名单访问限制 (CIDR)</label>
        <textarea id="drInAcl" rows="2" class="w-full bg-slate-800 border border-slate-700 rounded-lg p-2.5 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500" placeholder="10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.1/32">${(inbound?.allowedCidr || ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.1/32']).join(', ')}</textarea>
      </div>

      <div class="flex justify-between items-center pt-4 border-t border-slate-800">
        ${isEdit ? `<button type="button" class="btn-del-in text-xs text-rose-400 hover:text-rose-300 transition">🗑 删除网关</button>` : '<div></div>'}
        <div class="flex gap-2">
          <button type="button" class="btn-cancel px-4 py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">取消</button>
          <button type="submit" class="px-5 py-2 bg-cyan-600 hover:bg-cyan-500 text-white font-medium rounded-lg shadow-lg shadow-cyan-600/30">保存网关</button>
        </div>
      </div>
    `;

    form.querySelectorAll('input, select, textarea').forEach(el => el.addEventListener('change', () => { this.isDirty = true; }));
    form.querySelector('.btn-cancel').addEventListener('click', () => this.close());

    if (isEdit) {
      form.querySelector('.btn-del-in')?.addEventListener('click', async () => {
        if (!confirm(`确定删除接入网关 [${inbound.name}] 吗？`)) return;
        await apiCall(`inbounds?id=${encodeURIComponent(inbound.id)}`, { method: 'DELETE' });
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '网关已删除' });
      });
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const addPortsStr = form.querySelector('#drInAdditional').value;
      const addPorts = addPortsStr.split(/[,，]/).map(s => parseInt(s.trim())).filter(p => !isNaN(p) && p > 0);
      const aclStr = form.querySelector('#drInAcl').value;
      const acls = aclStr.split(/[,，\n]/).map(s => s.trim()).filter(Boolean);

      const payload = {
        id: form.querySelector('#drInId').value || undefined,
        name: form.querySelector('#drInName').value.trim(),
        type: 'port',
        enabled: true,
        listenPort: parseInt(form.querySelector('#drInPort').value),
        additionalPorts: addPorts,
        protocol: form.querySelector('#drInProto').value,
        pipelineId: form.querySelector('#drInPipeId').value,
        allowedCidr: acls
      };

      try {
        await apiCall('inbounds', { method: 'POST', body: payload });
        this.isDirty = false;
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '接入网关已生效！' });
      } catch (err) {
        alert('保存网关失败: ' + err.message);
      }
    });

    this.bodyEl.appendChild(form);
  }

  // 4. 抽屉: URI 协议链接多行批量导入
  openImport() {
    this.open('一键批量导入协议链接 (URI Parser)', '📥', 'import');

    const container = document.createElement('div');
    container.className = 'space-y-4 text-xs';
    container.innerHTML = `
      <p class="text-slate-400 text-[11px]">支持粘贴多行标准链接，自动解析协议、主机、端口、凭据、指纹与流控：</p>
      <div class="flex flex-wrap gap-1 text-[10px]">
        <span class="px-1.5 py-0.5 rounded bg-emerald-950/60 text-emerald-400 border border-emerald-800/40">vless://</span>
        <span class="px-1.5 py-0.5 rounded bg-purple-950/60 text-purple-400 border border-purple-800/40">trojan://</span>
        <span class="px-1.5 py-0.5 rounded bg-indigo-950/60 text-indigo-400 border border-indigo-800/40">ss://</span>
        <span class="px-1.5 py-0.5 rounded bg-amber-950/60 text-amber-400 border border-amber-800/40">socks5://</span>
        <span class="px-1.5 py-0.5 rounded bg-sky-950/60 text-sky-400 border border-sky-800/40">http://</span>
      </div>

      <textarea id="drImportText" rows="6" class="w-full bg-slate-800 border border-slate-700 rounded-lg p-3 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500" placeholder="粘贴链接至此，支持多行批量解析..."></textarea>

      <div id="drImportPreview" class="hidden p-3 rounded-lg bg-slate-950 border border-slate-800 space-y-2 max-h-48 overflow-y-auto custom-scroll">
        <div class="text-[11px] font-semibold text-cyan-400 flex justify-between">
          <span>解析结果 (<span id="drImportCount">0</span> 个节点)</span>
        </div>
        <div id="drImportList" class="space-y-1.5"></div>
      </div>

      <div id="drImportErrors" class="hidden p-2 rounded bg-rose-950/40 border border-rose-900/50 text-[10px] text-rose-400 font-mono"></div>

      <div class="flex justify-between items-center pt-3 border-t border-slate-800">
        <button type="button" id="btnDrDoPreview" class="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 text-cyan-400 rounded-lg">🔍 预览解析</button>
        <div class="flex gap-2">
          <button type="button" class="btn-cancel px-4 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">取消</button>
          <button type="button" id="btnDrConfirmImport" class="px-4 py-1.5 bg-cyan-600 hover:bg-cyan-500 text-white font-medium rounded-lg shadow">一键导入保存</button>
        </div>
      </div>
    `;

    container.querySelector('.btn-cancel').addEventListener('click', () => this.close());
    let parsedNodes = [];

    const doPreview = async () => {
      const text = container.querySelector('#drImportText').value.trim();
      if (!text) return alert('请先粘贴链接！');

      const btn = container.querySelector('#btnDrDoPreview');
      btn.textContent = '⏳ 解析中...';
      try {
        const res = await apiCall('upstreams/parse-link', { method: 'POST', body: { text } });
        parsedNodes = res.nodes || [];
        const errs = res.errors || [];

        container.querySelector('#drImportCount').textContent = parsedNodes.length;
        const list = container.querySelector('#drImportList');
        list.innerHTML = '';

        if (parsedNodes.length > 0) {
          container.querySelector('#drImportPreview').classList.remove('hidden');
          parsedNodes.forEach(n => {
            const row = document.createElement('div');
            row.className = 'p-1.5 rounded bg-slate-900 border border-slate-800 flex justify-between items-center text-[10px] font-mono';
            row.innerHTML = `
              <div class="flex items-center gap-1.5">
                <span class="px-1 py-0.2 bg-cyan-950 text-cyan-400 border border-cyan-800 rounded uppercase font-bold">${n.type}</span>
                <span class="text-slate-200">${n.name}</span>
                <span class="text-slate-500">(${n.server}:${n.port})</span>
              </div>
              <span class="text-emerald-400">✓ 就绪</span>
            `;
            list.appendChild(row);
          });
        }

        const errBox = container.querySelector('#drImportErrors');
        if (errs.length > 0) {
          errBox.classList.remove('hidden');
          errBox.innerHTML = errs.map(e => `<div>⚠️ ${e}</div>`).join('');
        } else {
          errBox.classList.add('hidden');
        }
      } catch (e) {
        alert('解析失败: ' + e.message);
      } finally {
        btn.textContent = '🔍 预览解析';
      }
    };

    container.querySelector('#btnDrDoPreview').addEventListener('click', doPreview);

    container.querySelector('#btnDrConfirmImport').addEventListener('click', async () => {
      if (parsedNodes.length === 0) {
        await doPreview();
      }
      if (parsedNodes.length === 0) return alert('未解析出任何有效节点！');

      const saveBtn = container.querySelector('#btnDrConfirmImport');
      saveBtn.textContent = '⏳ 保存中...';
      try {
        for (const n of parsedNodes) {
          await apiCall('upstreams', { method: 'POST', body: n });
        }
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: `成功导入 ${parsedNodes.length} 个节点！` });
      } catch (e) {
        alert('批量保存失败: ' + e.message);
      } finally {
        saveBtn.textContent = '一键导入保存';
      }
    });

    this.bodyEl.appendChild(container);
  }

  // 5. 抽屉: 用户态 TUN 虚拟网卡设置
  openTun() {
    this.open('透明代理网关设置 (User-Space TUN)', '🛡️', 'tun');
    const tun = store.tunStatus;

    const container = document.createElement('div');
    container.className = 'space-y-4 text-xs';
    container.innerHTML = `
      <div class="p-3 rounded-xl bg-slate-950 border border-slate-800 space-y-2">
        <div class="flex items-center justify-between">
          <span class="font-bold text-slate-200">宿主容器特权与虚拟网卡状态</span>
          <span class="px-2 py-0.5 rounded-full text-[10px] font-bold ${tun?.deviceReady && tun?.capNetAdminReady ? 'bg-emerald-950 text-emerald-400 border border-emerald-800' : 'bg-rose-950 text-rose-400 border border-rose-800'}">
            ${tun?.deviceReady && tun?.capNetAdminReady ? '🟢 特权就绪' : '🔴 缺少特权'}
          </span>
        </div>
        <p class="text-[11px] text-slate-400 font-mono">${tun?.deviceReady && tun?.capNetAdminReady ? '容器内 /dev/net/tun 与 CAP_NET_ADMIN 特权均就绪，支持全局用户态透明网关。' : '当前容器缺少 --cap-add=NET_ADMIN 或 /dev/net/tun 映射，TUN 透明网关受安全沙箱保护禁用。'}</p>
      </div>

      <div class="space-y-3">
        <div class="grid grid-cols-2 gap-3">
          <div>
            <label class="block text-slate-400 mb-1">虚拟网卡名</label>
            <input type="text" value="lwt-tun0" readonly class="w-full bg-slate-800/60 border border-slate-700 rounded-lg px-3 py-2 text-slate-300 font-mono text-[11px]">
          </div>
          <div>
            <label class="block text-slate-400 mb-1">用户态网络栈</label>
            <input type="text" value="Google gVisor Netstack (零污染)" readonly class="w-full bg-slate-800/60 border border-slate-700 rounded-lg px-3 py-2 text-cyan-300 font-mono text-[11px]">
          </div>
        </div>

        <div>
          <label class="block text-slate-400 mb-1">TUN 虚拟网段 (Fake-IP)</label>
          <input type="text" id="drTunSubnet" value="198.18.0.1/16" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500">
        </div>

        <div class="p-3 bg-slate-950/60 rounded-lg border border-slate-800">
          <label class="flex items-center gap-2 cursor-pointer text-slate-200">
            <input type="checkbox" id="drTunDns" checked class="rounded bg-slate-800 border-slate-700 text-cyan-500 focus:ring-0">
            <span class="font-bold">53 端口 DNS 自动劫持</span>
          </label>
          <p class="text-[10px] text-slate-500 mt-1">自动拦截本机及局域网 DNS 查询，精准下发 Fake-IP 实现全协议透明加速。</p>
        </div>
      </div>

      <div class="flex justify-end pt-3 border-t border-slate-800">
        <button type="button" class="btn-cancel px-4 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">关闭</button>
      </div>
    `;

    container.querySelector('.btn-cancel').addEventListener('click', () => this.close());
    this.bodyEl.appendChild(container);
  }

  // 6. 抽屉: 多跳链式代理拓扑编排
  openChain(chain = null) {
    const isEdit = !!(chain && chain.id);
    this.open(isEdit ? '编排多跳链式代理' : '新建多跳链式代理', '🔗', 'chain');

    const form = document.createElement('form');
    form.className = 'space-y-4 text-xs';
    form.innerHTML = `
      <input type="hidden" id="drChainId" value="${chain?.id || ''}">
      <div class="grid grid-cols-2 gap-3">
        <div>
          <label class="block text-slate-400 mb-1">链条名称</label>
          <input type="text" id="drChainName" required value="${chain?.name || ''}" placeholder="如: 亚太中继 + 美西固定落地" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
        </div>
        <div>
          <label class="block text-slate-400 mb-1">调度策略</label>
          <select id="drChainStrategy" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-purple-300 font-bold focus:outline-none focus:border-purple-500">
            <option value="auto" ${chain?.strategy !== 'manual' ? 'selected' : ''}>自适应动态寻优 (推荐: 支持固定出口)</option>
            <option value="manual" ${chain?.strategy === 'manual' ? 'selected' : ''}>手动严格静态穿透 (按指定顺序级联)</option>
          </select>
        </div>
      </div>

      <!-- 自动模式 -->
      <div id="drBoxChainAuto" class="p-3 bg-slate-950/60 rounded-xl border border-slate-800 space-y-3 ${chain?.strategy === 'manual' ? 'hidden' : ''}">
        <div>
          <label class="block text-slate-300 font-bold mb-1">🎯 可选固定落地出口节点 (Pinned Exit)</label>
          <select id="drChainFixedExit" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-slate-100 focus:outline-none focus:border-cyan-500">
            <option value="">🌐 (自动全网格优选，不固定出口)</option>
            ${store.upstreams.map(u => `<option value="${u.id}" ${chain?.fixedExitNodeId === u.id ? 'selected' : ''}>📡 ${u.name} (${u.server || 'WSS'})</option>`).join('')}
          </select>
          <p class="text-[10px] text-slate-500 mt-1">锁定落地出口 IP 不漂移；前置跳板池将自动挑选最低时延线路加速中继。</p>
        </div>
        <div>
          <label class="block text-slate-400 mb-1">前置候选跳板池 (多选参与中继寻优)</label>
          <div class="grid grid-cols-2 gap-2 bg-slate-800/60 p-2.5 rounded-lg border border-slate-800 max-h-28 overflow-y-auto custom-scroll" id="drChainHopsBox">
            ${store.upstreams.map(u => {
              const isChecked = (chain?.candidateHopNodeIds || []).includes(u.id);
              return `
                <label class="flex items-center gap-1.5 cursor-pointer text-slate-300 text-[11px]">
                  <input type="checkbox" value="${u.id}" ${isChecked ? 'checked' : ''} class="dr-chain-hop-cb rounded bg-slate-800 border-slate-700 text-purple-500 focus:ring-0">
                  <span class="truncate">${u.name}</span>
                </label>
              `;
            }).join('')}
          </div>
        </div>
      </div>

      <!-- 手动模式 -->
      <div id="drBoxChainManual" class="p-3 bg-slate-950/60 rounded-xl border border-slate-800 space-y-3 ${chain?.strategy === 'manual' ? '' : 'hidden'}">
        <div class="grid grid-cols-2 gap-2">
          <div>
            <label class="block text-slate-400 mb-1">第一跳 (前置中继节点)</label>
            <select id="drChainHop1" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-2.5 py-1.5 text-slate-100 focus:outline-none focus:border-cyan-500">
              ${store.upstreams.map(u => `<option value="${u.id}" ${chain?.hops?.[0] === u.id ? 'selected' : ''}>${u.name}</option>`).join('')}
            </select>
          </div>
          <div>
            <label class="block text-slate-400 mb-1">第二跳 (落地出口节点)</label>
            <select id="drChainHop2" class="w-full bg-slate-800 border border-slate-700 rounded-lg px-2.5 py-1.5 text-slate-100 focus:outline-none focus:border-cyan-500">
              ${store.upstreams.map(u => `<option value="${u.id}" ${chain?.hops?.[1] === u.id ? 'selected' : ''}>${u.name}</option>`).join('')}
            </select>
          </div>
        </div>
      </div>

      <div class="p-2.5 bg-slate-950/90 rounded-lg border border-purple-900/30 font-mono text-[11px] text-purple-300 text-center">
        客户端 ➔ <span class="text-amber-400">[前置跳板中继]</span> ➔ <span class="text-emerald-400">[落地出口]</span> ➔ 互联网
      </div>

      <div class="flex justify-between items-center pt-4 border-t border-slate-800">
        ${isEdit ? `<button type="button" class="btn-del-chain text-xs text-rose-400 hover:text-rose-300 transition">🗑 删除链条</button>` : '<div></div>'}
        <div class="flex gap-2">
          <button type="button" class="btn-cancel px-4 py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-lg">取消</button>
          <button type="submit" class="px-5 py-2 bg-purple-600 hover:bg-purple-500 text-white font-medium rounded-lg shadow-lg shadow-purple-600/30">保存链式配置</button>
        </div>
      </div>
    `;

    const selStrat = form.querySelector('#drChainStrategy');
    selStrat.addEventListener('change', () => {
      const isManual = selStrat.value === 'manual';
      form.querySelector('#drBoxChainAuto').classList.toggle('hidden', isManual);
      form.querySelector('#drBoxChainManual').classList.toggle('hidden', !isManual);
    });

    form.querySelectorAll('input, select').forEach(el => el.addEventListener('change', () => { this.isDirty = true; }));
    form.querySelector('.btn-cancel').addEventListener('click', () => this.close());

    if (isEdit) {
      form.querySelector('.btn-del-chain')?.addEventListener('click', async () => {
        if (!confirm(`确定删除代理链 [${chain.name}] 吗？`)) return;
        await apiCall(`chains?id=${encodeURIComponent(chain.id)}`, { method: 'DELETE' });
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '链式代理已删除' });
      });
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const strat = form.querySelector('#drChainStrategy').value;
      let hops = null;
      let hopCandidates = [];
      let fixedExit = '';

      if (strat === 'manual') {
        const h1 = form.querySelector('#drChainHop1').value;
        const h2 = form.querySelector('#drChainHop2').value;
        hops = [h1, h2];
      } else {
        fixedExit = form.querySelector('#drChainFixedExit').value;
        form.querySelectorAll('.dr-chain-hop-cb:checked').forEach(cb => {
          hopCandidates.push(cb.value);
        });
      }

      const payload = {
        id: form.querySelector('#drChainId').value || undefined,
        name: form.querySelector('#drChainName').value.trim(),
        strategy: strat,
        hops: hops,
        fixedExitNodeId: fixedExit,
        candidateHopNodeIds: hopCandidates
      };

      try {
        await apiCall('chains', { method: 'POST', body: payload });
        this.isDirty = false;
        await refreshAllData();
        this.close();
        eventBus.emit('toast', { type: 'success', message: '链式代理保存成功！' });
      } catch (err) {
        alert('保存链式代理失败: ' + err.message);
      }
    });

    this.bodyEl.appendChild(form);
  }
}
