// rule_builder.js - 可视化分流规则条目构建器 (Visual Rule Tag Builder & DSL)

import { apiCall } from './store.js';

export class VisualRuleBuilder {
  constructor(container, initialRules = []) {
    this.container = container;
    this.rules = Array.isArray(initialRules) ? JSON.parse(JSON.stringify(initialRules)) : [];
    this.isDslMode = false;
    this.cachedTemplates = [];
    this.init();
  }

  async init() {
    try {
      const res = await apiCall('routing-templates');
      this.cachedTemplates = res.templates || [];
    } catch (_) {}
    this.render();
  }

  getRules() {
    if (this.isDslMode) {
      this.syncFromDsl();
    }
    return this.rules;
  }

  setRules(rules) {
    this.rules = Array.isArray(rules) ? JSON.parse(JSON.stringify(rules)) : [];
    this.render();
  }

  render() {
    this.container.innerHTML = '';
    const wrapper = document.createElement('div');
    wrapper.className = 'space-y-3';

    // 顶部工具栏: 场景模板选择 + 模式切换
    const toolbar = document.createElement('div');
    toolbar.className = 'flex items-center justify-between gap-2 p-2 rounded-lg bg-slate-950/60 border border-slate-800 text-xs';
    toolbar.innerHTML = `
      <div class="flex items-center gap-1.5 flex-1">
        <span class="text-slate-400">📋 场景模板:</span>
        <select class="sel-rule-preset bg-slate-800 border border-slate-700 rounded px-2 py-1 text-cyan-300 text-[11px] focus:outline-none flex-1 max-w-[200px]">
          <option value="">-- 一键套用常用模板 --</option>
          ${this.cachedTemplates.map(t => `<option value="${t.id}">${t.name}</option>`).join('')}
        </select>
      </div>
      <button type="button" class="btn-toggle-dsl px-2 py-1 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 text-[11px] border border-slate-700">
        ${this.isDslMode ? '🎨 切换条目可视化' : '📝 切换纯文本代码'}
      </button>
    `;

    toolbar.querySelector('.sel-rule-preset').addEventListener('change', (e) => {
      const tpl = this.cachedTemplates.find(t => t.id === e.target.value);
      if (tpl && tpl.rules) {
        this.rules = JSON.parse(JSON.stringify(tpl.rules));
        this.render();
      }
    });

    toolbar.querySelector('.btn-toggle-dsl').addEventListener('click', () => {
      if (this.isDslMode) {
        this.syncFromDsl();
      }
      this.isDslMode = !this.isDslMode;
      this.render();
    });

    wrapper.appendChild(toolbar);

    if (this.isDslMode) {
      // 纯文本 DSL 模式
      const dslBox = document.createElement('div');
      const lines = this.rules.map(r => `${r.type}: ${r.value} -> ${r.action}`);
      dslBox.innerHTML = `
        <textarea class="rule-dsl-area w-full bg-slate-800 border border-slate-700 rounded-lg p-2.5 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500" rows="6" placeholder="每行一条，例如:\ndomain_suffix: openai.com -> proxy\ndomain_suffix: .cn -> direct\ndomain_keyword: adservice -> reject\nip_cidr: 223.5.5.0/24 -> direct">${lines.join('\n')}</textarea>
        <p class="text-[10px] text-slate-500 mt-1">格式: 类型: 规则值 -> 动作 (proxy | direct | reject)</p>
      `;
      wrapper.appendChild(dslBox);
    } else {
      // 可视化条目模式 (Chips)
      const listContainer = document.createElement('div');
      listContainer.className = 'space-y-1.5 max-h-56 overflow-y-auto custom-scroll pr-1';

      if (this.rules.length === 0) {
        listContainer.innerHTML = `<div class="p-3 text-center text-[11px] text-slate-500 border border-dashed border-slate-800 rounded-lg">暂无分流规则，点击下方添加或套用预设模板</div>`;
      } else {
        this.rules.forEach((rule, idx) => {
          const item = document.createElement('div');
          item.className = 'flex items-center gap-2 p-1.5 rounded-lg bg-slate-900 border border-slate-800 text-[11px]';
          item.innerHTML = `
            <select class="sel-type bg-slate-800 border border-slate-700 rounded px-1.5 py-1 text-slate-200 focus:outline-none">
              <option value="domain_suffix" ${rule.type === 'domain_suffix' ? 'selected' : ''}>域名后缀</option>
              <option value="domain_keyword" ${rule.type === 'domain_keyword' ? 'selected' : ''}>关键字</option>
              <option value="ip_cidr" ${rule.type === 'ip_cidr' ? 'selected' : ''}>IP网段(CIDR)</option>
            </select>
            <input type="text" class="inp-val flex-1 bg-slate-800 border border-slate-700 rounded px-2 py-1 text-slate-100 font-mono text-[11px] focus:outline-none focus:border-cyan-500" value="${rule.value || ''}" placeholder="如: openai.com 或 .cn">
            <span class="text-slate-500">➔</span>
            <select class="sel-act bg-slate-800 border border-slate-700 rounded px-1.5 py-1 font-bold ${this.getActionColor(rule.action)} focus:outline-none">
              <option value="proxy" ${rule.action === 'proxy' ? 'selected' : ''}>代理出网</option>
              <option value="direct" ${rule.action === 'direct' ? 'selected' : ''}>直连网络</option>
              <option value="reject" ${rule.action === 'reject' ? 'selected' : ''}>阻断拦截</option>
            </select>
            <div class="flex items-center gap-0.5">
              <button type="button" class="btn-move-up text-slate-500 hover:text-cyan-400 p-1 transition ${idx === 0 ? 'opacity-30 cursor-not-allowed' : ''}" title="提高优先级 (上移)">▲</button>
              <button type="button" class="btn-move-down text-slate-500 hover:text-cyan-400 p-1 transition ${idx === this.rules.length - 1 ? 'opacity-30 cursor-not-allowed' : ''}" title="降低优先级 (下移)">▼</button>
              <button type="button" class="btn-del-rule text-slate-500 hover:text-rose-400 p-1 transition" title="删除此规则">✕</button>
            </div>
          `;

          item.querySelector('.sel-type').addEventListener('change', (e) => { this.rules[idx].type = e.target.value; });
          item.querySelector('.inp-val').addEventListener('input', (e) => { this.rules[idx].value = e.target.value.trim(); });
          item.querySelector('.sel-act').addEventListener('change', (e) => {
            this.rules[idx].action = e.target.value;
            e.target.className = `sel-act bg-slate-800 border border-slate-700 rounded px-1.5 py-1 font-bold ${this.getActionColor(e.target.value)} focus:outline-none`;
          });
          item.querySelector('.btn-move-up')?.addEventListener('click', () => {
            if (idx > 0) {
              const temp = this.rules[idx];
              this.rules[idx] = this.rules[idx - 1];
              this.rules[idx - 1] = temp;
              this.render();
            }
          });
          item.querySelector('.btn-move-down')?.addEventListener('click', () => {
            if (idx < this.rules.length - 1) {
              const temp = this.rules[idx];
              this.rules[idx] = this.rules[idx + 1];
              this.rules[idx + 1] = temp;
              this.render();
            }
          });
          item.querySelector('.btn-del-rule').addEventListener('click', () => {
            this.rules.splice(idx, 1);
            this.render();
          });

          listContainer.appendChild(item);
        });
      }

      wrapper.appendChild(listContainer);

      // 底部追加按钮
      const addBar = document.createElement('div');
      addBar.className = 'flex justify-between items-center pt-1';
      addBar.innerHTML = `
        <button type="button" class="btn-add-chip text-xs px-2.5 py-1 rounded bg-slate-800 hover:bg-slate-700 text-cyan-400 border border-cyan-800/40 flex items-center gap-1 transition">
          + 添加单条分流规则
        </button>
        <span class="text-[10px] text-slate-500">共 ${this.rules.length} 条规则</span>
      `;
      addBar.querySelector('.btn-add-chip').addEventListener('click', () => {
        this.rules.push({ type: 'domain_suffix', value: '', action: 'proxy' });
        this.render();
      });

      wrapper.appendChild(addBar);
    }

    this.container.appendChild(wrapper);
  }

  syncFromDsl() {
    const area = this.container.querySelector('.rule-dsl-area');
    if (!area) return;
    const lines = area.value.split('\n');
    const parsed = [];
    for (const rawLine of lines) {
      const line = rawLine.trim();
      if (!line || line.startsWith('#') || line.startsWith('//')) continue;
      const parts = line.split('->');
      if (parts.length === 2) {
        const left = parts[0].trim();
        const action = parts[1].trim();
        const colonIdx = left.indexOf(':');
        if (colonIdx > 0) {
          const type = left.substring(0, colonIdx).trim();
          const value = left.substring(colonIdx + 1).trim();
          parsed.push({ type, value, action });
        }
      }
    }
    this.rules = parsed;
  }

  getActionColor(action) {
    if (action === 'direct') return 'text-emerald-400';
    if (action === 'reject') return 'text-rose-400';
    return 'text-cyan-400';
  }
}
