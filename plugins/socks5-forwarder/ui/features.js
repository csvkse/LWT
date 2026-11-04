// SOCKS5 边缘网关进阶特性：链接一键导入、分流规则模板、链式代理双模与透明网关
(function () {
  'use strict';

  // 挂载全局对象供 app.js 协同调用
  window.ForwarderFeatures = {
    templates: [],
    chains: [],
    tunStatus: null,
    apiCall: null, // 由 app.js 注入或自适应
    reloadProfiles: null
  };

  // 1. 初始化分流模板模块
  async function initRoutingTemplates(apiCall) {
    const sel = document.getElementById('selRoutingTemplate');
    if (!sel) return;

    try {
      const data = await apiCall('routing-templates');
      window.ForwarderFeatures.templates = data.templates || [];
      sel.innerHTML = '<option value="">-- 选择常用预设模板 --</option>';
      window.ForwarderFeatures.templates.forEach(tpl => {
        const opt = document.createElement('option');
        opt.value = tpl.id;
        opt.textContent = `${tpl.name} (${tpl.rules ? tpl.rules.length : 0} 条规则)`;
        sel.appendChild(opt);
      });
    } catch (err) {
      console.warn('获取分流模板失败:', err);
    }

    sel.addEventListener('change', () => {
      const selectedId = sel.value;
      if (!selectedId) return;
      const tpl = window.ForwarderFeatures.templates.find(t => t.id === selectedId);
      if (!tpl) return;

      // 自动填充分流配置
      const directCb = document.getElementById('inDirectPrivate');
      const actionSel = document.getElementById('inDefaultAction');
      const rulesArea = document.getElementById('inRoutingRules');

      if (directCb) directCb.checked = tpl.directPrivateNetwork;
      if (actionSel) actionSel.value = tpl.defaultAction || 'proxy';

      if (rulesArea && tpl.rules) {
        const lines = tpl.rules.map(r => `${r.type}: ${r.value} -> ${r.action}`);
        rulesArea.value = lines.join('\n');
      }
    });
  }

  // 2. 初始化链接一键与批量导入模块
  function initLinkImportModal(apiCall, reloadProfiles) {
    const btnOpen = document.getElementById('btnImportLink');
    const modal = document.getElementById('modalImportLink');
    const btnPreview = document.getElementById('btnDoParsePreview');
    const btnConfirm = document.getElementById('btnConfirmImport');
    const textArea = document.getElementById('importLinksText');
    const previewArea = document.getElementById('importPreviewArea');
    const parsedList = document.getElementById('importParsedList');
    const parsedCount = document.getElementById('importParsedCount');
    const errArea = document.getElementById('importErrorsArea');

    if (!btnOpen || !modal) return;

    let currentParsedNodes = [];

    btnOpen.addEventListener('click', () => {
      textArea.value = '';
      previewArea.classList.add('hidden');
      errArea.classList.add('hidden');
      currentParsedNodes = [];
      modal.classList.remove('hidden');
    });

    // 解析预览
    btnPreview.addEventListener('click', async () => {
      const text = textArea.value.trim();
      if (!text) {
        alert('请先粘贴待导入的标准协议链接！');
        return;
      }

      btnPreview.textContent = '⏳ 解析中...';
      try {
        const res = await apiCall('upstreams/parse-link', {
          method: 'POST',
          body: { text: text }
        });

        currentParsedNodes = res.nodes || [];
        const errors = res.errors || [];

        parsedCount.textContent = currentParsedNodes.length;
        parsedList.innerHTML = '';

        if (currentParsedNodes.length > 0) {
          previewArea.classList.remove('hidden');
          currentParsedNodes.forEach((node, idx) => {
            const item = document.createElement('div');
            item.className = 'p-2 rounded bg-slate-900 border border-slate-800 flex items-center justify-between';
            item.innerHTML = `
              <div class="flex items-center gap-2">
                <span class="px-1.5 py-0.5 rounded text-[9px] font-bold bg-cyan-950 text-cyan-300 border border-cyan-800">${node.type.toUpperCase()}</span>
                <span class="text-slate-200 font-semibold">${node.name}</span>
                <span class="text-slate-400">(${node.server}:${node.port})</span>
              </div>
              <span class="text-emerald-400">✓ 就绪</span>
            `;
            parsedList.appendChild(item);
          });
        } else {
          previewArea.classList.add('hidden');
        }

        if (errors.length > 0) {
          errArea.classList.remove('hidden');
          errArea.innerHTML = errors.map(e => `<div>⚠️ ${e}</div>`).join('');
        } else {
          errArea.classList.add('hidden');
        }
      } catch (err) {
        alert('解析失败: ' + err.message);
      } finally {
        btnPreview.textContent = '🔍 预览解析';
      }
    });

    // 确认导入并存入配置
    btnConfirm.addEventListener('click', async () => {
      if (currentParsedNodes.length === 0) {
        const text = textArea.value.trim();
        if (text) {
          await btnPreview.click();
        }
      }

      if (currentParsedNodes.length === 0) {
        alert('没有解析出任何有效节点，请检查链接格式');
        return;
      }

      btnConfirm.textContent = '⏳ 正在批量保存...';
      try {
        for (const node of currentParsedNodes) {
          await apiCall('upstreams', {
            method: 'POST',
            body: node
          });
        }
        modal.classList.add('hidden');
        if (reloadProfiles) await reloadProfiles();
      } catch (err) {
        alert('批量导入保存失败: ' + err.message);
      } finally {
        btnConfirm.textContent = '一键导入保存';
      }
    });
  }

  // 3. 初始化链式代理拓扑与管理模块 (支持固定落地出口模式)
  function initChainsModal(apiCall, reloadProfiles) {
    const btnOpen = document.getElementById('btnManageChains');
    const modal = document.getElementById('modalChains');
    const strategySel = document.getElementById('chainStrategy');
    const boxAuto = document.getElementById('boxChainAuto');
    const boxManual = document.getElementById('boxChainManual');
    const fixedExitSel = document.getElementById('chainFixedExit');
    const hopCandidatesBox = document.getElementById('chainHopCandidates');
    const manualHop1 = document.getElementById('chainManualHop1');
    const manualHop2 = document.getElementById('chainManualHop2');
    const previewBox = document.getElementById('chainTopologyPreview');
    const formChain = document.getElementById('formChain');
    const chainList = document.getElementById('chainList');
    const chainCount = document.getElementById('chainCount');

    if (!btnOpen || !modal) return;

    btnOpen.addEventListener('click', async () => {
      await refreshChainsList();
      populateUpstreamOptions();
      updateTopologyPreview();
      modal.classList.remove('hidden');
    });

    strategySel.addEventListener('change', () => {
      const isAuto = strategySel.value === 'auto';
      if (isAuto) {
        boxAuto.classList.remove('hidden');
        boxManual.classList.add('hidden');
      } else {
        boxAuto.classList.add('hidden');
        boxManual.classList.remove('hidden');
      }
      updateTopologyPreview();
    });

    fixedExitSel.addEventListener('change', updateTopologyPreview);

    function updateTopologyPreview() {
      const isAuto = strategySel.value === 'auto';
      if (isAuto) {
        const fixedText = fixedExitSel.options[fixedExitSel.selectedIndex]?.text || '';
        const isPinned = fixedExitSel.value !== '';
        if (isPinned) {
          previewBox.innerHTML = `<span>客户端</span> ➔ <span class="text-amber-400 font-bold">[前置跳板池 (自适应寻优/自愈)]</span> ➔ <span class="text-emerald-400 font-bold">[固定落地: ${fixedText.split(' ')[0]}]</span> ➔ <span>目标互联网</span>`;
        } else {
          previewBox.innerHTML = `<span>客户端</span> ➔ <span class="text-cyan-400 font-bold">[全网格动态矩阵寻优]</span> ➔ <span>目标互联网</span>`;
        }
      } else {
        const hop1Text = manualHop1.options[manualHop1.selectedIndex]?.text || '第一跳';
        const hop2Text = manualHop2.options[manualHop2.selectedIndex]?.text || '第二跳';
        previewBox.innerHTML = `<span>客户端</span> ➔ <span class="text-amber-400">[第一跳: ${hop1Text}]</span> ➔ <span class="text-purple-400">[第二跳: ${hop2Text}]</span> ➔ <span>目标互联网</span>`;
      }
    }

    function populateUpstreamOptions() {
      const upstreams = window.ForwarderFeatures.upstreams || [];

      // 填充固定出口下拉框
      fixedExitSel.innerHTML = '<option value="">🌐 (自动全网格优选，不固定出口)</option>';
      upstreams.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id;
        opt.textContent = `${u.name} [${u.type}]`;
        fixedExitSel.appendChild(opt);
      });

      // 填充手动第一跳/第二跳
      manualHop1.innerHTML = '';
      manualHop2.innerHTML = '';
      upstreams.forEach(u => {
        const opt1 = document.createElement('option');
        opt1.value = u.id;
        opt1.textContent = `${u.name} [${u.type}]`;
        manualHop1.appendChild(opt1);

        const opt2 = document.createElement('option');
        opt2.value = u.id;
        opt2.textContent = `${u.name} [${u.type}]`;
        manualHop2.appendChild(opt2);
      });
      if (manualHop2.options.length > 1) {
        manualHop2.selectedIndex = 1;
      }

      // 填充候选跳板复选框
      hopCandidatesBox.innerHTML = '';
      upstreams.forEach(u => {
        const label = document.createElement('label');
        label.className = 'flex items-center gap-1.5 text-[11px] text-slate-300 cursor-pointer';
        label.innerHTML = `
          <input type="checkbox" name="chainHopCandidate" value="${u.id}" checked class="rounded bg-slate-800 border-slate-700 text-cyan-500 focus:ring-0">
          <span class="truncate">${u.name}</span>
        `;
        hopCandidatesBox.appendChild(label);
      });
    }

    async function refreshChainsList() {
      try {
        const data = await apiCall('chains');
        window.ForwarderFeatures.chains = data.chains || [];
        chainCount.textContent = window.ForwarderFeatures.chains.length;
        chainList.innerHTML = '';

        if (window.ForwarderFeatures.chains.length === 0) {
          chainList.innerHTML = '<div class="text-[11px] text-slate-500 italic p-2 border border-dashed border-slate-800 rounded text-center">暂未创建链式代理配置</div>';
          return;
        }

        window.ForwarderFeatures.chains.forEach(c => {
          const item = document.createElement('div');
          item.className = 'p-2.5 rounded-lg bg-slate-950 border border-slate-800 flex items-center justify-between text-xs';
          const strategyTag = c.strategy === 'auto'
            ? (c.fixedExitNodeId ? '<span class="px-1.5 py-0.5 rounded bg-emerald-950 text-emerald-400 border border-emerald-800 text-[10px]">自适应 (固定出口)</span>' : '<span class="px-1.5 py-0.5 rounded bg-cyan-950 text-cyan-400 border border-cyan-800 text-[10px]">自适应 (网格优选)</span>')
            : '<span class="px-1.5 py-0.5 rounded bg-purple-950 text-purple-400 border border-purple-800 text-[10px]">静态手动</span>';

          item.innerHTML = `
            <div class="space-y-0.5">
              <div class="flex items-center gap-2">
                ${strategyTag}
                <span class="font-bold text-slate-200">${c.name}</span>
              </div>
              <div class="text-[10px] text-slate-400 font-mono">ID: ${c.id}</div>
            </div>
            <button class="btn-del-chain px-2 py-1 bg-slate-800 hover:bg-rose-900/40 text-rose-400 rounded text-[11px] transition" data-id="${c.id}">删除</button>
          `;
          chainList.appendChild(item);
        });

        chainList.querySelectorAll('.btn-del-chain').forEach(btn => {
          btn.addEventListener('click', async () => {
            const id = btn.getAttribute('data-id');
            if (!confirm(`确定要删除链式配置 [${id}] 吗？`)) return;
            try {
              await apiCall(`chains?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
              await refreshChainsList();
            } catch (err) {
              alert('删除链式配置失败: ' + err.message);
            }
          });
        });
      } catch (err) {
        console.warn('获取链式列表失败:', err);
      }
    }

    formChain.addEventListener('submit', async (e) => {
      e.preventDefault();
      const strategy = strategySel.value;
      const chain = {
        id: document.getElementById('chainEditId').value || undefined,
        name: document.getElementById('chainName').value.trim(),
        strategy: strategy
      };

      if (strategy === 'auto') {
        chain.fixedExitNodeId = fixedExitSel.value || undefined;
        const checkedHops = Array.from(document.querySelectorAll('input[name="chainHopCandidate"]:checked')).map(cb => cb.value);
        chain.candidateHopNodeIds = checkedHops;
      } else {
        const hop1 = manualHop1.value;
        const hop2 = manualHop2.value;
        if (!hop1 || !hop2) {
          alert('手动模式必须指定第一跳与第二跳节点！');
          return;
        }
        chain.hops = [hop1, hop2];
      }

      try {
        await apiCall('chains', { method: 'POST', body: chain });
        formChain.reset();
        await refreshChainsList();
        updateTopologyPreview();
      } catch (err) {
        alert('保存链式代理配置失败: ' + err.message);
      }
    });
  }

  // 4. 初始化透明网关 (TUN / TProxy) 面板
  function initTunModal(apiCall) {
    const btnOpen = document.getElementById('btnTunStatus');
    const modal = document.getElementById('modalTun');
    const badge = document.getElementById('tunStatusBadge');
    const msg = document.getElementById('tunStatusMsg');

    if (!btnOpen || !modal) return;

    btnOpen.addEventListener('click', async () => {
      badge.textContent = '探查中...';
      badge.className = 'px-2 py-0.5 rounded-full text-[10px] font-bold bg-slate-800 text-slate-400';
      msg.textContent = '正在探查宿主容器 /dev/net/tun 设备与 CAP_NET_ADMIN 特权...';
      modal.classList.remove('hidden');

      try {
        const res = await apiCall('system/tun-status');
        window.ForwarderFeatures.tunStatus = res;

        if (res.deviceAvailable && res.hasPrivilege) {
          badge.textContent = '🟢 设备就绪';
          badge.className = 'px-2 py-0.5 rounded-full text-[10px] font-bold bg-emerald-500/20 text-emerald-400 border border-emerald-500/30';
          msg.textContent = res.statusMessage || '🟢 /dev/net/tun 虚拟设备正常就绪，支持用户态 gVisor 透明接管';
        } else {
          badge.textContent = '⚠️ 环境受限';
          badge.className = 'px-2 py-0.5 rounded-full text-[10px] font-bold bg-amber-500/20 text-amber-400 border border-amber-500/30';
          msg.textContent = res.statusMessage || '⚠️ 未检测到有效 TUN 驱动设备或缺少 CAP_NET_ADMIN 特权';
        }
      } catch (err) {
        badge.textContent = '❌ 探查异常';
        badge.className = 'px-2 py-0.5 rounded-full text-[10px] font-bold bg-rose-500/20 text-rose-400';
        msg.textContent = '连接探查接口失败: ' + err.message;
      }
    });
  }

  // 暴露启动函数
  window.ForwarderFeatures.init = function (apiCall, reloadProfiles, upstreams) {
    window.ForwarderFeatures.apiCall = apiCall;
    window.ForwarderFeatures.reloadProfiles = reloadProfiles;
    window.ForwarderFeatures.upstreams = upstreams;

    initRoutingTemplates(apiCall);
    initLinkImportModal(apiCall, reloadProfiles);
    initChainsModal(apiCall, reloadProfiles);
    initTunModal(apiCall);
  };
})();
