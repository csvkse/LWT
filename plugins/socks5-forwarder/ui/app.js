// SOCKS5 客户端边缘中继网关控制台原生逻辑
(function () {
  'use strict';

  // 1. 环境与 API 基地址自动适配
  const urlParams = new URLSearchParams(window.location.search);
  const token = urlParams.get('token') || '';
  let apiBase = '';

  if (window.location.pathname.includes('/plugins/socks5-forwarder')) {
    apiBase = window.location.pathname.replace(/\/ui\/.*|\/index\.html|\/?$/, '') + '/api';
  } else if (window.location.pathname.includes('/api/Plugins/socks5-forwarder')) {
    apiBase = '/plugins/socks5-forwarder/api';
  } else {
    apiBase = '/api';
  }

  let state = {
    upstreams: [],
    inbounds: [],
    loading: false
  };

  async function apiCall(endpoint, options = {}) {
    options.headers = options.headers || {};
    if (token) {
      options.headers['X-LWT-Token'] = token;
      options.headers['Authorization'] = 'Bearer ' + token;
    }
    if (options.body && typeof options.body === 'object') {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(options.body);
    }
    const cleanEndpoint = endpoint.replace(/^\/+/, '');
    const res = await fetch(`${apiBase}/${cleanEndpoint}`, options);
    if (!res.ok) {
      const errText = await res.text();
      throw new Error(`HTTP ${res.status}: ${errText}`);
    }
    return res.json();
  }

  // 2. 数据拉取与渲染
  async function loadData() {
    try {
      const data = await apiCall('profiles');
      state.upstreams = data.upstreams || [];
      state.inbounds = data.inbounds || [];
      if (window.ForwarderFeatures) {
        window.ForwarderFeatures.upstreams = state.upstreams;
      }
      renderUpstreams();
      renderInbounds();
    } catch (err) {
      console.error('加载中继配置失败:', err);
    }
  }

  function formatBytes(bytes) {
    if (!bytes || bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }

  function getProtocolBadge(type) {
    const t = (type || 'lwt_wss').toLowerCase();
    switch (t) {
      case 'socks5':
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-amber-500/20 text-amber-300 border border-amber-500/30">SOCKS5</span>';
      case 'http':
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-sky-500/20 text-sky-300 border border-sky-500/30">HTTP</span>';
      case 'trojan':
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-purple-500/20 text-purple-300 border border-purple-500/30">TROJAN</span>';
      case 'vless':
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-emerald-500/20 text-emerald-300 border border-emerald-500/30">VLESS</span>';
      case 'shadowsocks':
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-indigo-500/20 text-indigo-300 border border-indigo-500/30">SHADOWSOCKS</span>';
      default:
        return '<span class="px-2 py-0.5 text-[10px] font-bold rounded bg-cyan-500/20 text-cyan-300 border border-cyan-500/30">LWT-WSS</span>';
    }
  }

  function getNodeTargetDisplay(node) {
    if (node.server && node.port) {
      return `${node.server}:${node.port}`;
    }
    return node.url || '127.0.0.1';
  }

  // 渲染上游节点
  function renderUpstreams() {
    const list = document.getElementById('upstreamList');
    document.getElementById('upstreamCount').textContent = state.upstreams.length;
    list.innerHTML = '';

    if (state.upstreams.length === 0) {
      list.innerHTML = `
        <div class="col-span-full p-4 rounded-xl border border-dashed border-slate-800 text-center text-slate-500 text-xs">
          暂无已配置的上游服务节点，请点击右上角 "+ 添加上游服务节点"
        </div>`;
      return;
    }

    state.upstreams.forEach(node => {
      const card = document.createElement('div');
      card.className = 'p-3.5 rounded-xl bg-slate-900 border border-slate-800 hover:border-slate-700 transition flex flex-col justify-between gap-2.5 shadow-sm';
      
      let statusBadge = '<span class="px-2 py-0.5 text-[10px] rounded-full bg-slate-800 text-slate-400">未知</span>';
      if (node.status === 'healthy') {
        statusBadge = `<span class="px-2 py-0.5 text-[10px] rounded-full bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">🟢 ${node.latencyMs} ms</span>`;
      } else if (node.status === 'degraded') {
        statusBadge = `<span class="px-2 py-0.5 text-[10px] rounded-full bg-amber-500/20 text-amber-400 border border-amber-500/30">🟡 ${node.latencyMs} ms</span>`;
      } else if (node.status === 'dead') {
        const errTitle = node.lastError ? ` title="${escapeHtml(node.lastError)}"` : '';
        statusBadge = `<span class="px-2 py-0.5 text-[10px] rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/30 cursor-help"${errTitle}>🔴 离线</span>`;
      }

      const errSnippet = node.status === 'dead' && node.lastError ? `
        <div class="text-[10px] text-rose-400/90 font-mono truncate bg-rose-950/30 px-1.5 py-0.5 rounded border border-rose-900/30" title="${escapeHtml(node.lastError)}">
          ⚠️ ${escapeHtml(node.lastError)}
        </div>` : '';

      const targetText = getNodeTargetDisplay(node);
      const protoBadge = getProtocolBadge(node.type);

      card.innerHTML = `
        <div class="flex items-start justify-between gap-2">
          <div class="truncate">
            <div class="flex items-center gap-1.5">
              ${protoBadge}
              <h4 class="font-bold text-xs text-slate-200 truncate">${escapeHtml(node.name)}</h4>
            </div>
            <div class="text-[11px] font-mono text-cyan-400/80 truncate mt-1" title="${escapeHtml(targetText)}">${escapeHtml(targetText)}</div>
          </div>
          <div>${statusBadge}</div>
        </div>

        ${errSnippet}

        <div class="flex items-center justify-between pt-2 border-t border-slate-800/80 text-[11px]">
          <span class="text-slate-500 text-[10px]">${node.skipTlsVerify ? '⚠️ 免证书校验' : '🔒 TLS 校验'}</span>
          <div class="flex items-center gap-1.5">
            <button class="btn-test-up px-2 py-0.8 bg-slate-800 hover:bg-slate-700 text-cyan-400 rounded transition" data-id="${node.id}">⚡ 测速</button>
            <button class="btn-edit-up px-2 py-0.8 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded transition" data-id="${node.id}">⚙</button>
            <button class="btn-del-up px-2 py-0.8 bg-slate-800 hover:bg-rose-900/40 text-rose-400 rounded transition" data-id="${node.id}">🗑</button>
          </div>
        </div>
      `;
      list.appendChild(card);
    });

    // 绑定卡片操作按钮
    list.querySelectorAll('.btn-test-up').forEach(btn => {
      btn.addEventListener('click', async () => {
        const id = btn.getAttribute('data-id');
        btn.textContent = '...';
        try {
          await apiCall(`upstreams/test?id=${encodeURIComponent(id)}`, { method: 'POST' });
          await loadData();
        } catch (e) {
          alert('测速失败: ' + e.message);
        }
      });
    });

    list.querySelectorAll('.btn-edit-up').forEach(btn => {
      btn.addEventListener('click', () => {
        const id = btn.getAttribute('data-id');
        const node = state.upstreams.find(n => n.id === id);
        if (node) openUpstreamModal(node);
      });
    });

    list.querySelectorAll('.btn-del-up').forEach(btn => {
      btn.addEventListener('click', async () => {
        if (!confirm('确定要删除该上游节点吗？关联此节点的入站中继将无法出网。')) return;
        const id = btn.getAttribute('data-id');
        try {
          await apiCall(`upstreams?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
          await loadData();
        } catch (e) {
          alert('删除失败: ' + e.message);
        }
      });
    });
  }

  // 渲染本地入站中继规则
  function renderInbounds() {
    const list = document.getElementById('inboundList');
    document.getElementById('inboundCount').textContent = state.inbounds.length;
    list.innerHTML = '';

    if (state.inbounds.length === 0) {
      list.innerHTML = `
        <div class="col-span-full p-6 rounded-xl border border-dashed border-slate-800 text-center text-slate-500 text-xs">
          暂无入站中继规则，请点击右上角 "+ 新建入站中继" 开启本地端口中继
        </div>`;
      return;
    }

    const hostIP = window.location.hostname || '127.0.0.1';

    state.inbounds.forEach(p => {
      const card = document.createElement('div');
      card.className = 'p-4 rounded-xl bg-slate-900/90 border border-slate-800 flex flex-col justify-between gap-3 shadow-md relative overflow-hidden';
      
      const isRunning = p.status === 'running' && p.enabled;
      const statusPill = isRunning
        ? '<span class="px-2 py-0.5 text-xs font-semibold rounded-full bg-emerald-500/20 text-emerald-400 border border-emerald-500/30 flex items-center gap-1"><span class="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse"></span> 运行中</span>'
        : (p.status === 'error'
          ? `<span class="px-2 py-0.5 text-xs font-semibold rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/30" title="${escapeHtml(p.lastError)}">⚠️ 端口异常</span>`
          : '<span class="px-2 py-0.5 text-xs font-semibold rounded-full bg-slate-800 text-slate-400">⏹ 已停止</span>');

      const aclPreview = (p.allowedCidr || []).slice(0, 3).join(', ') + ((p.allowedCidr || []).length > 3 ? '...' : '');

      const allPorts = [p.listenPort, ...(p.additionalPorts || [])].filter((v, i, a) => v && a.indexOf(v) === i);
      const portBadges = allPorts.map(pt => `<span class="text-xs px-2 py-0.5 rounded font-mono font-bold bg-cyan-950/80 text-cyan-300 border border-cyan-800/40">:${pt}</span>`).join(' ');
      const lanAccessCodes = allPorts.map(pt => `<code class="font-mono text-slate-200 select-all">${hostIP}:${pt}</code>`).join('<span class="text-slate-600 mx-1">|</span>');

      // 分流策略状态
      const rp = p.routePolicy || {};
      const directPrivatePill = rp.directPrivateNetwork !== false
        ? '<span class="text-emerald-400 font-medium">✓ 私网直连</span>'
        : '<span class="text-slate-500">✕ 私网走代理</span>';
      const ruleCount = (rp.rules || []).length;
      const rulePill = ruleCount > 0
        ? `<span class="text-cyan-400 font-medium">分流: ${ruleCount} 条</span>`
        : '<span class="text-slate-500">全量出站</span>';

      card.innerHTML = `
        <div>
          <div class="flex items-start justify-between gap-3">
            <div>
              <div class="flex items-center gap-2 flex-wrap">
                <h3 class="font-bold text-sm text-slate-100">${escapeHtml(p.name)}</h3>
                ${portBadges}
              </div>
              <p class="text-[11px] text-slate-400 mt-0.5">协议模式: <span class="text-slate-300 font-medium">${(p.protocol || 'mixed').toUpperCase()}</span> | 调度: <span class="text-slate-300">${p.upstreamStrategy || 'single'}</span></p>
            </div>
            <div>${statusPill}</div>
          </div>

          <div class="mt-3 grid grid-cols-2 gap-2 text-[11px] bg-slate-950/60 p-2.5 rounded-lg border border-slate-800/80">
            <div>
              <span class="text-slate-500 block text-[10px]">客户端 IP 白名单 (CIDR)</span>
              <span class="font-mono text-slate-300 truncate block mt-0.5" title="${(p.allowedCidr||[]).join(', ')}">${aclPreview || '全网放开'}</span>
            </div>
            <div>
              <span class="text-slate-500 block text-[10px]">智能分流路由策略</span>
              <div class="flex items-center gap-2 mt-0.5">${directPrivatePill} | ${rulePill}</div>
            </div>
          </div>

          <div class="mt-2 grid grid-cols-2 gap-2 text-[11px] bg-slate-950/40 p-2 rounded border border-slate-800/60">
            <div>
              <span class="text-slate-500 text-[10px]">上行流量:</span>
              <span class="font-mono text-slate-300 ml-1">↑ ${formatBytes(p.totalBytesTx)}</span>
            </div>
            <div>
              <span class="text-slate-500 text-[10px]">下行流量:</span>
              <span class="font-mono text-slate-300 ml-1">↓ ${formatBytes(p.totalBytesRx)}</span>
            </div>
          </div>

          <div class="mt-2.5 bg-slate-800/40 px-2.5 py-1.5 rounded border border-slate-800/60 flex items-center justify-between text-[11px]">
            <div class="truncate text-slate-400 flex items-center gap-1.5 flex-wrap">
              <span class="text-cyan-400 font-medium whitespace-nowrap">局域网接入:</span> 
              <div class="flex items-center gap-1 flex-wrap">${lanAccessCodes}</div>
            </div>
            <button class="btn-copy-proxy text-xs text-cyan-400 hover:text-cyan-300 ml-2 whitespace-nowrap" data-proxy="socks5://${hostIP}:${p.listenPort}">复制主端口</button>
          </div>
        </div>

        <div class="flex items-center justify-between pt-2.5 border-t border-slate-800 text-xs">
          <span class="text-[10px] text-slate-500">活跃连接: <b class="text-cyan-400">${p.activeConns || 0}</b></span>
          <div class="flex items-center gap-2">
            <button class="btn-toggle-in px-3 py-1 rounded font-medium transition ${isRunning ? 'bg-orange-600/20 text-orange-400 hover:bg-orange-600/30' : 'bg-emerald-600/20 text-emerald-400 hover:bg-emerald-600/30'}" data-id="${p.id}">
              ${isRunning ? '⏹ 停止' : '▶ 启动'}
            </button>
            <button class="btn-edit-in px-2.5 py-1 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded" data-id="${p.id}">⚙ 编辑</button>
            <button class="btn-del-in px-2.5 py-1 bg-slate-800 hover:bg-rose-900/40 text-rose-400 rounded" data-id="${p.id}">🗑</button>
          </div>
        </div>
      `;
      list.appendChild(card);
    });

    // 绑定事件
    list.querySelectorAll('.btn-toggle-in').forEach(btn => {
      btn.addEventListener('click', async () => {
        const id = btn.getAttribute('data-id');
        btn.textContent = '...';
        try {
          await apiCall(`inbounds/toggle?id=${encodeURIComponent(id)}`, { method: 'POST' });
          await loadData();
        } catch (e) {
          alert('切换状态失败: ' + e.message);
          await loadData();
        }
      });
    });

    list.querySelectorAll('.btn-edit-in').forEach(btn => {
      btn.addEventListener('click', () => {
        const id = btn.getAttribute('data-id');
        const p = state.inbounds.find(x => x.id === id);
        if (p) openInboundModal(p);
      });
    });

    list.querySelectorAll('.btn-del-in').forEach(btn => {
      btn.addEventListener('click', async () => {
        if (!confirm('确定要删除该入站规则吗？')) return;
        const id = btn.getAttribute('data-id');
        try {
          await apiCall(`inbounds?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
          await loadData();
        } catch (e) {
          alert('删除失败: ' + e.message);
        }
      });
    });

    list.querySelectorAll('.btn-copy-proxy').forEach(btn => {
      btn.addEventListener('click', () => {
        const text = btn.getAttribute('data-proxy');
        navigator.clipboard.writeText(text).then(() => {
          btn.textContent = '已复制!';
          setTimeout(() => { btn.textContent = '复制'; }, 1500);
        });
      });
    });
  }

  // 3. 模态框逻辑
  function updateUpstreamFormVisibility(type) {
    const t = (type || 'lwt_wss').toLowerCase();
    const boxWss = document.getElementById('boxWss');
    const boxHostPort = document.getElementById('boxHostPort');
    const boxUserPass = document.getElementById('boxUserPass');
    const boxSecret = document.getElementById('boxSecret');
    const boxUuid = document.getElementById('boxUuid');
    const boxTls = document.getElementById('boxTls');
    const lblSecret = document.getElementById('lblSecret');

    boxWss.classList.add('hidden');
    boxHostPort.classList.add('hidden');
    boxUserPass.classList.add('hidden');
    boxSecret.classList.add('hidden');
    boxUuid.classList.add('hidden');
    boxTls.classList.add('hidden');

    if (t === 'lwt_wss') {
      boxWss.classList.remove('hidden');
      boxTls.classList.remove('hidden');
    } else {
      boxHostPort.classList.remove('hidden');
      if (t === 'socks5' || t === 'http') {
        boxUserPass.classList.remove('hidden');
      } else if (t === 'trojan') {
        lblSecret.textContent = 'Trojan 密码 (Password)';
        boxSecret.classList.remove('hidden');
        boxTls.classList.remove('hidden');
      } else if (t === 'vless') {
        boxUuid.classList.remove('hidden');
        boxTls.classList.remove('hidden');
      } else if (t === 'shadowsocks') {
        lblSecret.textContent = 'Shadowsocks 预共享密钥 (Secret Key)';
        boxSecret.classList.remove('hidden');
      }
    }
  }

  function openUpstreamModal(node = null) {
    const modal = document.getElementById('modalUpstream');
    document.getElementById('modalUpstreamTitle').textContent = node ? '编辑上游服务节点' : '添加上游服务节点';
    document.getElementById('upId').value = node ? node.id : '';
    document.getElementById('upName').value = node ? node.name : '';
    const nodeType = node ? (node.type || 'lwt_wss') : 'lwt_wss';
    document.getElementById('upType').value = nodeType;

    document.getElementById('upUrl').value = node ? (node.url || '') : '';
    document.getElementById('upToken').value = node ? (node.token || '') : '';
    document.getElementById('upServer').value = node ? (node.server || '') : '';
    document.getElementById('upPort').value = node && node.port ? node.port : (nodeType === 'socks5' ? 1080 : (nodeType === 'http' ? 8080 : 443));
    document.getElementById('upUsername').value = node ? (node.username || '') : '';
    document.getElementById('upPassword').value = node ? (node.password || '') : '';
    document.getElementById('upSecret').value = node ? (node.password || '') : '';
    document.getElementById('upUuid').value = node ? (node.uuid || '') : '';
    document.getElementById('upSni').value = node ? (node.sni || '') : '';
    document.getElementById('upSkipTls').checked = node ? Boolean(node.skipTlsVerify) : true;

    updateUpstreamFormVisibility(nodeType);
    modal.classList.remove('hidden');
  }

  function openInboundModal(p = null) {
    const modal = document.getElementById('modalInbound');
    document.getElementById('modalInboundTitle').textContent = p ? '编辑入站中继规则' : '新建入站中继规则';
    document.getElementById('inId').value = p ? p.id : '';
    document.getElementById('inName').value = p ? p.name : '';
    document.getElementById('inPort').value = p ? p.listenPort : (10800 + state.inbounds.length + 1);
    document.getElementById('inAdditionalPorts').value = p && p.additionalPorts && p.additionalPorts.length > 0 ? p.additionalPorts.join(', ') : '';
    document.getElementById('inProtocol').value = p ? p.protocol : 'mixed';
    document.getElementById('inStrategy').value = p ? p.upstreamStrategy : 'single';
    document.getElementById('inAcl').value = p ? (p.allowedCidr || []).join(', ') : '10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.1/32';

    // 智能分流策略字段填充
    const rp = (p && p.routePolicy) ? p.routePolicy : {};
    document.getElementById('inDirectPrivate').checked = rp.directPrivateNetwork !== false;
    document.getElementById('inDefaultAction').value = rp.defaultAction || 'proxy';

    const rulesFormatted = (rp.rules || []).map(r => `${r.type}: ${r.value} -> ${r.action}`).join('\n');
    document.getElementById('inRoutingRules').value = rulesFormatted;

    // 动态生成候选上游复选框
    const container = document.getElementById('inboundUpstreamCheckboxes');
    container.innerHTML = '';
    const selectedNodes = new Set(p ? (p.upstreamNodes || []) : state.upstreams.map(n => n.id));
    state.upstreams.forEach(n => {
      const label = document.createElement('label');
      label.className = 'flex items-center gap-1.5 text-slate-300 cursor-pointer truncate';
      label.innerHTML = `
        <input type="checkbox" name="inUpstreamNode" value="${n.id}" ${selectedNodes.has(n.id) ? 'checked' : ''} class="rounded bg-slate-800 border-slate-700 text-cyan-500 focus:ring-0">
        <span class="truncate">${escapeHtml(n.name)}</span>
      `;
      container.appendChild(label);
    });

    modal.classList.remove('hidden');
  }

  function closeModals() {
    ['modalUpstream', 'modalInbound', 'modalImportLink', 'modalChains', 'modalTun'].forEach(id => {
      const el = document.getElementById(id);
      if (el) el.classList.add('hidden');
    });
  }

  // 4. 事件绑定
  document.getElementById('btnRefresh').addEventListener('click', loadData);
  document.getElementById('btnAddUpstream').addEventListener('click', () => openUpstreamModal());
  document.getElementById('btnAddInbound').addEventListener('click', () => openInboundModal());

  document.querySelectorAll('.modal-close').forEach(btn => btn.addEventListener('click', closeModals));

  document.getElementById('upType').addEventListener('change', (e) => {
    updateUpstreamFormVisibility(e.target.value);
  });

  // CIDR 快捷填充按钮
  document.getElementById('btnAclLan').addEventListener('click', () => {
    document.getElementById('inAcl').value = '10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.1/8, ::1/128';
  });
  document.getElementById('btnAclLocal').addEventListener('click', () => {
    document.getElementById('inAcl').value = '127.0.0.1/32, ::1/128';
  });
  document.getElementById('btnAclAll').addEventListener('click', () => {
    document.getElementById('inAcl').value = '*';
  });

  // 表单提交: 上游节点
  document.getElementById('formUpstream').addEventListener('submit', async (e) => {
    e.preventDefault();
    const type = document.getElementById('upType').value;
    const node = {
      id: document.getElementById('upId').value || undefined,
      name: document.getElementById('upName').value.trim(),
      type: type,
      sni: document.getElementById('upSni').value.trim(),
      skipTlsVerify: document.getElementById('upSkipTls').checked
    };

    if (type === 'lwt_wss') {
      node.url = document.getElementById('upUrl').value.trim();
      node.token = document.getElementById('upToken').value.trim();
    } else {
      node.server = document.getElementById('upServer').value.trim();
      node.port = parseInt(document.getElementById('upPort').value, 10) || 1080;
      if (type === 'socks5' || type === 'http') {
        node.username = document.getElementById('upUsername').value.trim();
        node.password = document.getElementById('upPassword').value.trim();
      } else if (type === 'trojan' || type === 'shadowsocks') {
        node.password = document.getElementById('upSecret').value.trim();
      } else if (type === 'vless') {
        node.uuid = document.getElementById('upUuid').value.trim();
      }
    }

    try {
      await apiCall('upstreams', { method: 'POST', body: node });
      closeModals();
      await loadData();
    } catch (err) {
      alert('保存失败: ' + err.message);
    }
  });

  // 表单提交: 入站中继
  document.getElementById('formInbound').addEventListener('submit', async (e) => {
    e.preventDefault();
    const checkedNodes = Array.from(document.querySelectorAll('input[name="inUpstreamNode"]:checked')).map(cb => cb.value);
    if (checkedNodes.length === 0) {
      alert('请至少勾选一个关联的上游服务器节点！');
      return;
    }

    const rawAcl = document.getElementById('inAcl').value.trim();
    const allowedCidr = rawAcl ? rawAcl.split(/[\n,;]+/).map(s => s.trim()).filter(Boolean) : ['*'];

    const rawAdditional = document.getElementById('inAdditionalPorts').value.trim();
    const additionalPorts = rawAdditional
      ? rawAdditional.split(/[\s,;]+/).map(s => parseInt(s.trim(), 10)).filter(pt => !isNaN(pt) && pt > 0 && pt <= 65535)
      : [];

    // 解析分流规则列表
    const rawRulesText = document.getElementById('inRoutingRules').value.trim();
    const parsedRules = [];
    if (rawRulesText) {
      const lines = rawRulesText.split('\n');
      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        // 匹配 "type: value -> action"
        const parts = trimmed.split('->');
        if (parts.length >= 2) {
          const left = parts[0].trim();
          const action = parts[1].trim();
          const colonIdx = left.indexOf(':');
          if (colonIdx > 0) {
            const ruleType = left.substring(0, colonIdx).trim();
            const ruleVal = left.substring(colonIdx + 1).trim();
            parsedRules.push({
              type: ruleType,
              value: ruleVal,
              action: action
            });
          }
        }
      }
    }

    const profile = {
      id: document.getElementById('inId').value || undefined,
      name: document.getElementById('inName').value.trim(),
      enabled: true,
      listenPort: parseInt(document.getElementById('inPort').value, 10),
      additionalPorts: additionalPorts,
      protocol: document.getElementById('inProtocol').value,
      upstreamStrategy: document.getElementById('inStrategy').value,
      upstreamNodes: checkedNodes,
      allowedCidr: allowedCidr,
      routePolicy: {
        directPrivateNetwork: document.getElementById('inDirectPrivate').checked,
        defaultAction: document.getElementById('inDefaultAction').value,
        rules: parsedRules
      }
    };

    try {
      await apiCall('inbounds', { method: 'POST', body: profile });
      closeModals();
      await loadData();
    } catch (err) {
      alert('保存规则失败: ' + err.message);
    }
  });

  function escapeHtml(str) {
    if (!str) return '';
    return String(str).replace(/[&<>"']/g, s => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[s]);
  }

  // 初始加载与自动轮询
  loadData();
  setInterval(loadData, 5000);

  if (window.ForwarderFeatures && window.ForwarderFeatures.init) {
    window.ForwarderFeatures.init(apiCall, loadData, state.upstreams);
  }
})();
