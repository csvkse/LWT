// SOCKS5 边缘中继网关：分流线路编排流水线 (Routing Pipelines) 模块
(function () {
  'use strict';

  window.ForwarderPipelines = {
    pipelines: [],
    apiCall: null,
    reload: null
  };

  // 初始化流水线模块
  window.ForwarderPipelines.init = function (apiCall, reloadProfiles) {
    window.ForwarderPipelines.apiCall = apiCall;
    window.ForwarderPipelines.reload = reloadProfiles;

    const btnAdd = document.getElementById('btnAddPipeline');
    const modal = document.getElementById('modalPipeline');
    const form = document.getElementById('formPipeline');
    const selPreset = document.getElementById('selPipePreset');

    if (btnAdd && modal) {
      btnAdd.addEventListener('click', () => {
        openPipelineModal(null);
      });
    }

    if (selPreset) {
      selPreset.addEventListener('change', () => {
        const tplId = selPreset.value;
        if (!tplId) return;
        const templates = (window.ForwarderFeatures && window.ForwarderFeatures.templates) || [];
        const tpl = templates.find(t => t.id === tplId);
        if (!tpl) return;

        document.getElementById('pipeDirectPrivate').checked = tpl.directPrivateNetwork;
        document.getElementById('pipeDefaultAction').value = tpl.defaultAction || 'proxy';
        if (tpl.rules) {
          const lines = tpl.rules.map(r => `${r.type}: ${r.value} -> ${r.action}`);
          document.getElementById('pipeRules').value = lines.join('\n');
        }
      });
    }

    if (form) {
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const rawRulesText = document.getElementById('pipeRules').value.trim();
        const parsedRules = [];
        if (rawRulesText) {
          const lines = rawRulesText.split('\n');
          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed || trimmed.startsWith('#')) continue;
            const parts = trimmed.split('->');
            if (parts.length >= 2) {
              const left = parts[0].trim();
              const action = parts[1].trim();
              const colonIdx = left.indexOf(':');
              if (colonIdx > 0) {
                parsedRules.push({
                  type: left.substring(0, colonIdx).trim(),
                  value: left.substring(colonIdx + 1).trim(),
                  action: action
                });
              }
            }
          }
        }

        const pipe = {
          id: document.getElementById('pipeId').value || undefined,
          name: document.getElementById('pipeName').value.trim(),
          description: document.getElementById('pipeDesc').value.trim(),
          directPrivateNetwork: document.getElementById('pipeDirectPrivate').checked,
          defaultAction: document.getElementById('pipeDefaultAction').value,
          defaultOutbound: document.getElementById('pipeDefaultOutbound').value,
          rules: parsedRules
        };

        try {
          await apiCall('pipelines', { method: 'POST', body: pipe });
          modal.classList.add('hidden');
          if (reloadProfiles) await reloadProfiles();
        } catch (err) {
          alert('保存线路失败: ' + err.message);
        }
      });
    }
  };

  // 打开新建/编辑线路弹窗
  function openPipelineModal(pipe) {
    const modal = document.getElementById('modalPipeline');
    const title = document.getElementById('modalPipeTitle');
    const form = document.getElementById('formPipeline');
    const selOutbound = document.getElementById('pipeDefaultOutbound');
    const selPreset = document.getElementById('selPipePreset');

    if (!modal) return;
    form.reset();

    // 填充预设模板下拉框
    if (selPreset) {
      selPreset.innerHTML = '<option value="">-- 选择常用预设模板一键套用 --</option>';
      const templates = (window.ForwarderFeatures && window.ForwarderFeatures.templates) || [];
      templates.forEach(t => {
        const opt = document.createElement('option');
        opt.value = t.id;
        opt.textContent = `${t.name} (${t.rules ? t.rules.length : 0} 条规则)`;
        selPreset.appendChild(opt);
      });
    }

    // 填充默认出站下拉框 (上游单节点 + 链式代理)
    if (selOutbound) {
      selOutbound.innerHTML = '<option value="">(遵循默认上游调度)</option>';
      const upstreams = (window.ForwarderFeatures && window.ForwarderFeatures.upstreams) || [];
      upstreams.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id;
        opt.textContent = `📡 节点: ${u.name} [${u.type}]`;
        selOutbound.appendChild(opt);
      });

      const chains = (window.ForwarderFeatures && window.ForwarderFeatures.chains) || [];
      chains.forEach(c => {
        const opt = document.createElement('option');
        opt.value = c.id;
        opt.textContent = `🔗 链式: ${c.name} [${c.strategy}]`;
        selOutbound.appendChild(opt);
      });
    }

    if (pipe) {
      title.textContent = `编辑分流线路: ${pipe.name}`;
      document.getElementById('pipeId').value = pipe.id;
      document.getElementById('pipeName').value = pipe.name;
      document.getElementById('pipeDesc').value = pipe.description || '';
      document.getElementById('pipeDirectPrivate').checked = pipe.directPrivateNetwork;
      document.getElementById('pipeDefaultAction').value = pipe.defaultAction || 'proxy';
      if (pipe.defaultOutbound) selOutbound.value = pipe.defaultOutbound;
      if (pipe.rules) {
        document.getElementById('pipeRules').value = pipe.rules.map(r => `${r.type}: ${r.value} -> ${r.action}`).join('\n');
      }
    } else {
      title.textContent = '新建分流线路流水线';
      document.getElementById('pipeId').value = '';
      document.getElementById('pipeDirectPrivate').checked = true;
      document.getElementById('pipeDefaultAction').value = 'proxy';
      document.getElementById('pipeRules').value = '';
    }

    modal.classList.remove('hidden');
  }

  // 渲染分流线路卡片列表
  window.ForwarderPipelines.render = function (pipelines) {
    const list = document.getElementById('pipelineList');
    const count = document.getElementById('pipelineCount');
    if (!list) return;

    window.ForwarderPipelines.pipelines = pipelines || [];
    if (count) count.textContent = window.ForwarderPipelines.pipelines.length;
    list.innerHTML = '';

    if (window.ForwarderPipelines.pipelines.length === 0) {
      list.innerHTML = `
        <div class="col-span-full p-4 rounded-xl border border-dashed border-slate-800 text-center text-slate-500 text-xs">
          暂无分流线路，请点击右上角 "+ 新增分流线路"
        </div>`;
      return;
    }

    window.ForwarderPipelines.pipelines.forEach(p => {
      const card = document.createElement('div');
      card.className = 'p-3.5 rounded-xl bg-slate-900 border border-slate-800 hover:border-slate-700 transition flex flex-col justify-between gap-2.5 shadow-sm';

      const isDefault = p.id === 'pipe-default';
      const defaultBadge = isDefault
        ? '<span class="px-1.5 py-0.5 rounded bg-cyan-950 text-cyan-300 border border-cyan-800 text-[10px] font-bold">默认全局</span>'
        : '<span class="px-1.5 py-0.5 rounded bg-slate-800 text-slate-400 text-[10px]">自定义</span>';

      const ruleCount = p.rules ? p.rules.length : 0;
      const privateText = p.directPrivateNetwork ? '✓ 私网直连' : '✕ 私网走代理';

      card.innerHTML = `
        <div class="flex items-start justify-between gap-2">
          <div class="truncate">
            <div class="flex items-center gap-1.5">
              ${defaultBadge}
              <h4 class="font-bold text-xs text-slate-200 truncate">${escapeHtml(p.name)}</h4>
            </div>
            <p class="text-[11px] text-slate-400 truncate mt-1">${escapeHtml(p.description || '无描述')}</p>
          </div>
          <span class="px-2 py-0.5 rounded-full bg-slate-800 text-slate-300 text-[10px] font-mono">${ruleCount} 条规则</span>
        </div>

        <div class="p-2 rounded-lg bg-slate-950/70 border border-slate-800 text-[10px] space-y-1">
          <div class="flex items-center justify-between text-slate-400">
            <span>默认出站: <span class="font-mono text-cyan-300 font-semibold">${escapeHtml(p.defaultOutbound || p.defaultAction || 'proxy')}</span></span>
            <span class="text-emerald-400 font-mono">${privateText}</span>
          </div>
        </div>

        <div class="flex items-center justify-between pt-2 border-t border-slate-800/80 text-[11px]">
          <span class="text-[10px] text-slate-500 font-mono">ID: ${p.id}</span>
          <div class="flex items-center gap-1.5">
            <button class="btn-edit-pipe px-2 py-0.8 bg-slate-800 hover:bg-slate-700 text-slate-200 rounded text-xs transition" data-id="${p.id}">⚙ 编辑</button>
            ${isDefault ? '' : `<button class="btn-del-pipe px-2 py-0.8 bg-slate-800 hover:bg-rose-900/40 text-rose-400 rounded text-xs transition" data-id="${p.id}">🗑</button>`}
          </div>
        </div>
      `;

      list.appendChild(card);
    });

    // 绑定编辑与删除
    list.querySelectorAll('.btn-edit-pipe').forEach(btn => {
      btn.addEventListener('click', () => {
        const id = btn.getAttribute('data-id');
        const pipe = window.ForwarderPipelines.pipelines.find(p => p.id === id);
        if (pipe) openPipelineModal(pipe);
      });
    });

    list.querySelectorAll('.btn-del-pipe').forEach(btn => {
      btn.addEventListener('click', async () => {
        const id = btn.getAttribute('data-id');
        if (!confirm(`确定要删除分流线路 [${id}] 吗？绑定此线路的入站将自动回退至默认线路。`)) return;
        try {
          await window.ForwarderPipelines.apiCall(`pipelines?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
          if (window.ForwarderPipelines.reload) await window.ForwarderPipelines.reload();
        } catch (err) {
          alert('删除分流线路失败: ' + err.message);
        }
      });
    });
  };

  function escapeHtml(str) {
    if (!str) return '';
    return String(str).replace(/[&<>"']/g, s => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[s]);
  }
})();
