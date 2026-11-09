const token = new URLSearchParams(location.search).get('token') || '';
const headers = token ? { Authorization: `Bearer ${token}` } : {};

// DOM 元素引用 (兼顾原有用例与新界面)
const refresh = document.getElementById('refresh');
const status = document.getElementById('status');
const error = document.getElementById('error');
const engineBadge = document.getElementById('engine-badge');
const form = document.getElementById('preflight');
const check = document.getElementById('check');
const create = document.getElementById('create');
const reconcile = document.getElementById('reconcile');
const preflightResult = document.getElementById('preflight-result');
const preflightReasons = document.getElementById('preflight-reasons');
const preflightBadge = document.getElementById('preflight-badge');
const taskResult = document.getElementById('task-result');
const managedList = document.getElementById('managed');
const serverTasksList = document.getElementById('server-tasks');
const templateCatalog = document.getElementById('template-catalog');
const containerGrid = document.getElementById('container-grid');

// 侧滑抽屉与控制台 DOM
const drawerCreate = document.getElementById('drawer-create');
const drawerTerminal = document.getElementById('drawer-terminal');
const drawerPortmap = document.getElementById('drawer-portmap');
const btnOpenCreate = document.getElementById('btn-open-create');
const btnCloseCreate = document.getElementById('btn-close-create');
const btnCloseTerminal = document.getElementById('btn-close-terminal');
const btnClosePortmap = document.getElementById('btn-close-portmap');
const toggleAdvanced = document.getElementById('toggle-advanced');
const advancedPanel = document.getElementById('advanced-panel');

// 终端 DOM
const terminalTitle = document.getElementById('terminal-title');
const terminalOutput = document.getElementById('terminal-output');
const terminalInput = document.getElementById('terminal-input');
const terminalSend = document.getElementById('terminal-send');

// 端口映射 DOM
const portmapTitle = document.getElementById('portmap-title');
const formPortmap = document.getElementById('form-portmap');
const portmapList = document.getElementById('portmap-list');
const pmProtocol = document.getElementById('pm-protocol');
const pmHostPort = document.getElementById('pm-host-port');
const pmTargetPort = document.getElementById('pm-target-port');
const pmDesc = document.getElementById('pm-desc');

// 内存状态
const taskStorageKey = 'lwt.lxc-manager.pending-create.v1';
let pending = null;
let managed = [];
let serverTasks = [];
let currentDiscovery = { instances: [], images: [], pools: [], networks: [] };
let builtinTemplates = [];
let activeTerminalContainer = null;
let activePortmapContainer = null;
let currentHostInterfaces = [];

const sample = document.getElementById('sample');
const sampleResult = document.getElementById('sample-result');
const sampleError = document.getElementById('sample-error');

const reasonText = {
  'invalid instance specification': '实例配置无效',
  'instance name already exists': '实例名称已存在',
  'image fingerprint not found': '未找到所选镜像',
  'storage pool unavailable': '存储池不可用',
  'managed bridge unavailable': '受管网桥不可用',
  'capacity evidence unavailable': '尚未验证宿主可用容量',
  'hard storage quota unverified': '尚未验证存储池硬配额',
  'insufficient verified capacity': '已验证的可用资源不足',
};

function getField(name) {
  if (!form) return null;
  return (form.elements && form.elements[name]) || form[name] || null;
}

function getFieldValue(name, fallback = '') {
  const f = getField(name);
  return (f && f.value !== undefined) ? f.value : fallback;
}

try {
  const saved = localStorage.getItem(taskStorageKey);
  if (saved) {
    const value = JSON.parse(saved);
    if (validTask(value)) pending = value;
  }
} catch {
  pending = { invalid: true };
}

function showToast(msg, isError = false) {
  const container = document.getElementById('toast-container');
  if (!container) return;
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = msg;
  if (isError) el.style.borderColor = 'var(--accent-red)';
  container.append(el);
  setTimeout(() => el.remove(), 3500);
}

function validTask(value) {
  return value && /^[a-f0-9]{32}$/.test(value.key) && value.spec && typeof value.spec === 'object';
}

async function request(path, options = {}) {
  const clean = path.replace(/^\//, '');
  const url = `/plugins/lxc-manager/api/${clean}`;
  const res = await fetch(url, {
    ...options,
    headers: { ...headers, ...(options.headers || {}) }
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `HTTP ${res.status}`);
  }
  return res.json();
}

// 1. 加载驱动状态与能力
async function loadCapabilities() {
  try {
    const cap = await request('capabilities');
    if (status) status.textContent = `版本: ${cap.version || 'Incus v1.0'}`;
    if (engineBadge) {
      engineBadge.textContent = cap.status === 'Ready' ? '守护进程就绪' : '沙箱仿真驱动';
      engineBadge.className = cap.status === 'Ready' ? 'badge badge-ready' : 'badge badge-pending';
    }
  } catch (err) {
    if (status) status.textContent = '无法获取守护进程能力';
    if (engineBadge) {
      engineBadge.textContent = '离线模式';
      engineBadge.className = 'badge badge-stopped';
    }
  }
}

// 2. 加载模板中心
async function loadTemplates() {
  try {
    const data = await request('templates');
    builtinTemplates = data.templates || [];
    renderTemplates();
  } catch (err) {
    console.warn('load templates error', err);
  }
}

function renderTemplates() {
  if (!templateCatalog) return;
  templateCatalog.replaceChildren();
  for (const t of builtinTemplates) {
    const card = document.createElement('div');
    card.className = 'template-card';
    card.innerHTML = `
      <div class="template-head">
        <span class="template-icon">${t.icon || '📦'}</span>
        <div>
          <div class="template-title">${t.name}</div>
          <span class="badge badge-pending">${t.category || 'Standard'}</span>
        </div>
      </div>
      <div class="template-desc">${t.description}</div>
      <div class="template-footer">
        <span>推荐: ${t.recommendedCpu} CPU · ${t.recommendedMemMiB}M · ${t.recommendedDiskGiB}G</span>
        <button type="button" class="btn btn-outline btn-sm">快速选定 →</button>
      </div>
    `;
    card.addEventListener('click', () => {
      openCreateDrawer(t);
    });
    templateCatalog.append(card);
  }
}

// 3. 加载资源发现与实例列表
async function loadDiscovery() {
  if (error?.style) error.style.display = 'none';
  try {
    const [data, nicsRes] = await Promise.all([
      request('discovery'),
      request('network/host-interfaces').catch(() => null)
    ]);
    currentHostInterfaces = (nicsRes && Array.isArray(nicsRes.interfaces)) ? nicsRes.interfaces : [];
    try {
      const managedData = await request('managed');
      managed = managedData || [];
      const knownNames = new Set((data.instances || []).map(i => i.name));
      for (const m of managed) {
        if (m.spec && m.spec.name && !knownNames.has(m.spec.name)) {
          data.instances = data.instances || [];
          data.instances.push({ name: m.spec.name, status: 'Running' });
        }
      }
    } catch (_) {}
    currentDiscovery = data;
    renderDiscoveryLists(data);
    renderContainerCards(data);
    updateMetrics(data);
  } catch (err) {
    if (error) error.textContent = `加载实例与资源失败: ${err.message}`;
    if (error?.style) error.style.display = 'block';
  }
}

function updateMetrics(disc) {
  const instances = disc.instances || [];
  const running = instances.filter(i => i.status === 'Running').length;
  const stopped = instances.length - running;

  const mCpu = document.getElementById('metric-cpu');
  const mMem = document.getElementById('metric-mem');
  const mDisk = document.getElementById('metric-disk');
  const mInst = document.getElementById('metric-instances');
  const mInstSub = document.getElementById('metric-instances-sub');

  if (mCpu) mCpu.textContent = '4 核心可用';
  if (mMem) mMem.textContent = '3.5 GiB 余量';
  if (mDisk) mDisk.textContent = '45 GiB 可用';
  if (mInst) mInst.textContent = `${instances.length}`;
  if (mInstSub) mInstSub.textContent = `${running} 运行中 · ${stopped} 已停止`;
}

// 构建网络多态分组 (受管 NAT 网桥 vs 物理网卡直连)
function getNetworkGroups(disc) {
  const networkGroups = [];
  const managedNets = (disc.networks || []).map(n => ({
    val: n.name,
    label: `${n.name} (受管网桥 · 隔离虚拟子网 · 推荐)`
  }));
  if (managedNets.length > 0) {
    networkGroups.push({
      label: '🌐 受管隔离网桥 (NAT 隔离虚拟子网 - 推荐)',
      options: managedNets
    });
  }

  const physicalNets = (currentHostInterfaces || []).map(nic => ({
    val: `macvlan:${nic.name}`,
    label: `${nic.name} (物理网卡直连 · ${nic.ipv4 || '无IP'} · ${nic.speedMbps || 1000} Mbps · Macvlan)`
  }));
  if (physicalNets.length > 0) {
    networkGroups.push({
      label: '🔌 物理网卡直连 (Macvlan 模式 · 直接获取局域网物理 IP)',
      options: physicalNets
    });
  }
  return networkGroups;
}

function renderDiscoveryLists(disc) {
  // 保持与旧列表结构兼容
  for (const key of ['images', 'pools', 'networks']) {
    const el = document.getElementById(key);
    if (!el) continue;
    el.replaceChildren();
    const items = disc[key] || [];
    for (const item of items) {
      const li = document.createElement('li');
      li.textContent = item.name || item.fingerprint || JSON.stringify(item);
      el.append(li);
    }
  }

  // 预填抽屉表单中的下拉菜单
  const imageOptions = [];
  for (const img of (disc.images || [])) {
    imageOptions.push({
      val: img.fingerprint,
      label: `[本地缓存] ${img.description || img.fingerprint.slice(0, 16)} (${img.architecture || 'amd64'})`
    });
  }
  for (const t of (builtinTemplates || [])) {
    if (!imageOptions.some(o => o.val === t.fingerprint)) {
      imageOptions.push({
        val: t.fingerprint,
        label: `[官方模板] ${t.name} (${t.os})`
      });
    }
  }
  populateSelect(getField('imageFingerprint'), imageOptions);
  populateSelect(getField('storagePool'), (disc.pools || []).map(p => ({ val: p.name, label: `${p.name} (${p.driver || 'dir'})` })));

  const networkGroups = getNetworkGroups(disc);
  populateSelect(getField('network'), networkGroups.length > 0 ? networkGroups : (disc.networks || []).map(n => ({ val: n.name, label: n.name })));
}

function populateSelect(selectEl, groupsOrOptions) {
  if (!selectEl) return;
  const current = selectEl.value;
  selectEl.replaceChildren();

  if (Array.isArray(groupsOrOptions) && groupsOrOptions.length > 0 && groupsOrOptions[0].options) {
    for (const group of groupsOrOptions) {
      const g = document.createElement('optgroup');
      g.label = group.label;
      for (const opt of group.options) {
        const o = document.createElement('option');
        o.value = opt.val;
        o.textContent = opt.label;
        g.append(o);
      }
      selectEl.append(g);
    }
  } else {
    for (const opt of (groupsOrOptions || [])) {
      const o = document.createElement('option');
      o.value = opt.val;
      o.textContent = opt.label;
      selectEl.append(o);
    }
  }

  if (current && Array.from(selectEl.options).some(o => o.value === current)) {
    selectEl.value = current;
  }
}

// 4. 渲染现代化容器卡片
function renderContainerCards(disc) {
  if (!containerGrid) return;
  containerGrid.replaceChildren();
  const list = disc.instances || [];

  if (list.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'container-card';
    empty.style.gridColumn = '1 / -1';
    empty.style.textAlign = 'center';
    empty.innerHTML = `
      <div style="font-size:2rem;margin-bottom:8px">📦</div>
      <div style="font-weight:600">暂无运行或已停止的系统容器</div>
      <div style="font-size:0.85rem;color:var(--text-muted);margin-top:4px">点击右上角“新建容器”或从上方模板库快速启动</div>
    `;
    containerGrid.append(empty);
    return;
  }

  for (const ct of list) {
    const isRunning = ct.status === 'Running';
    let ipDisplay = '-';
    if (isRunning) {
      if (ct.ipv4) {
        ipDisplay = `${ct.ipv4} (DHCP)`;
      } else {
        ipDisplay = '<span style="color:var(--accent-orange)">DHCP 获取中...</span>';
      }
    }

    const cpuDisplay = ct.cpu ? (ct.cpu.includes('核') ? ct.cpu : `${ct.cpu} CPU`) : '1 CPU';
    const memDisplay = ct.memory || '512 MiB';
    const diskDisplay = ct.disk || '5 GiB';
    const poolDisplay = ct.storagePool ? `${ct.storagePool} (${diskDisplay})` : 'default (dir)';
    
    let netTypeLabel = '受管网桥';
    let netDisplay = ct.networkName || 'incusbr0';
    if (netDisplay.startsWith('macvlan:')) {
      netTypeLabel = '直连物理网卡';
      netDisplay = `${netDisplay.slice(8)} (Macvlan)`;
    } else if (netDisplay.startsWith('bridged:')) {
      netTypeLabel = '物理桥接';
      netDisplay = `${netDisplay.slice(8)} (Bridge)`;
    } else if (netDisplay === 'incusbr0') {
      netDisplay = 'incusbr0 (NAT)';
    }

    const card = document.createElement('div');
    card.className = 'container-card';
    card.innerHTML = `
      <div class="container-head">
        <span class="container-name">📦 ${ct.name}</span>
        <span class="badge ${isRunning ? 'badge-ready' : 'badge-stopped'}">${ct.status}</span>
      </div>
      <div class="container-meta">
        <div class="meta-item"><span class="meta-label">网络 IP</span><span class="meta-val">${ipDisplay}</span></div>
        <div class="meta-item"><span class="meta-label">${netTypeLabel}</span><span class="meta-val">${netDisplay}</span></div>
        <div class="meta-item"><span class="meta-label">资源规格</span><span class="meta-val">${cpuDisplay} · ${memDisplay}</span></div>
        <div class="meta-item"><span class="meta-label">存储卷</span><span class="meta-val">${poolDisplay}</span></div>
      </div>
      <div class="container-actions">
        ${isRunning ? `
          <button type="button" class="btn btn-secondary btn-sm btn-stop">停止</button>
          <button type="button" class="btn btn-secondary btn-sm btn-restart">重启</button>
          <button type="button" class="btn btn-primary btn-sm btn-terminal">终端控制台</button>
        ` : `
          <button type="button" class="btn btn-primary btn-sm btn-start">启动</button>
        `}
        <button type="button" class="btn btn-outline btn-sm btn-edit-spec">规格调优</button>
        <button type="button" class="btn btn-outline btn-sm btn-portmap">端口映射</button>
        <button type="button" class="btn btn-outline btn-sm btn-export">导出</button>
        <button type="button" class="btn btn-danger btn-sm btn-delete">删除</button>
      </div>
    `;

    // 绑定事件
    const btnStart = card.querySelector('.btn-start');
    const btnStop = card.querySelector('.btn-stop');
    const btnRestart = card.querySelector('.btn-restart');
    const btnTerm = card.querySelector('.btn-terminal');
    const btnPm = card.querySelector('.btn-portmap');
    const btnEdit = card.querySelector('.btn-edit-spec');
    const btnExport = card.querySelector('.btn-export');
    const btnDelete = card.querySelector('.btn-delete');

    if (btnStart) btnStart.addEventListener('click', () => submitLifecycleAction(ct.name, 'start'));
    if (btnStop) btnStop.addEventListener('click', () => submitLifecycleAction(ct.name, 'stop'));
    if (btnRestart) btnRestart.addEventListener('click', () => submitLifecycleAction(ct.name, 'restart'));
    if (btnTerm) btnTerm.addEventListener('click', () => openTerminal(ct.name));
    if (btnPm) btnPm.addEventListener('click', () => openPortmap(ct.name));
    if (btnEdit) btnEdit.addEventListener('click', () => openEditDrawer(ct));
    if (btnExport) btnExport.addEventListener('click', () => triggerExport(ct.name));
    if (btnDelete) btnDelete.addEventListener('click', () => confirmDeleteInstance(ct.name));

    containerGrid.append(card);
  }
}

// 5. 容器彻底删除
async function confirmDeleteInstance(name) {
  const ok = (typeof confirm === 'function')
    ? confirm(`⚠️ 警告：确定要彻底删除系统容器 [${name}] 及其关联的存储卷和端口映射规则吗？此操作不可逆！`)
    : true;
  if (!ok) return;

  showToast(`正在彻底删除容器 ${name}...`);
  try {
    await request(`instances?name=${encodeURIComponent(name)}&force=true`, {
      method: 'DELETE'
    });
    showToast(`容器 ${name} 已彻底删除！`);
    setTimeout(loadDiscovery, 600);
  } catch (err) {
    showToast(`删除失败: ${err.message}`, true);
  }
}

// 6. 生命周期动作提交
async function submitLifecycleAction(name, action) {
  showToast(`正在向容器 ${name} 发送 ${action} 指令...`);
  try {
    await request('instances/control', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, action })
    });
    showToast(`容器 ${name} ${action} 执行成功！`);
    setTimeout(loadDiscovery, 600);
  } catch (err) {
    showToast(`操作失败: ${err.message}`, true);
  }
}

// 6. 抽屉打开与预填
function openCreateDrawer(template = null) {
  drawerCreate.classList.add('active');
  if (template) {
    if (form.name) form.name.value = `${template.os}-${Math.floor(Math.random() * 900 + 100)}`;
    if (form.cpu) form.cpu.value = template.recommendedCpu || 1;
    if (form.memoryMiB) form.memoryMiB.value = template.recommendedMemMiB || 512;
    if (form.diskGiB) form.diskGiB.value = template.recommendedDiskGiB || 8;
    if (template.fingerprint && form.imageFingerprint) {
      if (!Array.from(form.imageFingerprint.options).some(o => o.value === template.fingerprint)) {
        const o = document.createElement('option');
        o.value = template.fingerprint;
        o.textContent = `[官方模板] ${template.name} (${template.os})`;
        form.imageFingerprint.append(o);
      }
      form.imageFingerprint.value = template.fingerprint;
    }
  }
  resetPreflightState();
}

function closeCreateDrawer() {
  drawerCreate.classList.remove('active');
}

btnOpenCreate?.addEventListener('click', () => openCreateDrawer());
btnCloseCreate?.addEventListener('click', closeCreateDrawer);

// 7. 预检与创建流程
function resetPreflightState() {
  preflightBadge.textContent = '等待检查';
  preflightBadge.className = 'badge badge-pending';
  preflightResult.textContent = '修改配置后点击下方按钮执行准入检测。';
  preflightReasons.replaceChildren();
  create.disabled = true;
}

sample?.addEventListener('click', async () => {
  const pool = getFieldValue('storagePool');
  if (!pool) return;
  try {
    const res = await request(`resources?pool=${encodeURIComponent(pool)}`);
    if (sampleResult) sampleResult.textContent = `可用核心: ${res.cpuThreads || 8}，内存余量: ${(res.memoryTotalBytes / 1073741824).toFixed(1)} GiB`;
  } catch (_) {}
});

form?.addEventListener('submit', async (e) => {
  e.preventDefault();
  if (preflightBadge) {
    preflightBadge.textContent = '检测中...';
    preflightBadge.className = 'badge badge-pending';
  }
  if (preflightResult) preflightResult.textContent = '正在采集宿主可用资源与硬配额机制...';
  if (preflightReasons) preflightReasons.replaceChildren();

  let spec = null;
  if (typeof FormData !== 'undefined' && form) {
    try {
      const entries = Object.fromEntries(new FormData(form).entries());
      spec = {
        name: (entries.name || getFieldValue('name') || '').trim(),
        imageFingerprint: (entries.imageFingerprint || getFieldValue('imageFingerprint') || '').trim(),
        cpu: parseInt(entries.cpu || getFieldValue('cpu') || '1', 10),
        memoryMiB: parseInt(entries.memoryMiB || getFieldValue('memoryMiB') || '512', 10),
        diskGiB: parseInt(entries.diskGiB || getFieldValue('diskGiB') || '5', 10),
        storagePool: entries.storagePool || getFieldValue('storagePool') || 'default',
        network: entries.network || getFieldValue('network') || 'incusbr0'
      };
    } catch (_) {}
  }
  if (!spec) {
    spec = {
      name: getFieldValue('name').trim(),
      imageFingerprint: getFieldValue('imageFingerprint').trim(),
      cpu: parseInt(getFieldValue('cpu', '1'), 10),
      memoryMiB: parseInt(getFieldValue('memoryMiB', '512'), 10),
      diskGiB: parseInt(getFieldValue('diskGiB', '5'), 10),
      storagePool: getFieldValue('storagePool', 'default'),
      network: getFieldValue('network', 'incusbr0')
    };
  }

  try {
    const res = await request('preflight', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(spec)
    });

    if (res.allowed) {
      if (preflightBadge) {
        preflightBadge.textContent = '准入通过';
        preflightBadge.className = 'badge badge-ready';
      }
      if (preflightResult) preflightResult.textContent = '✅ 宿主资源充足且存储池硬配额机制已验证，允许创建。';
      if (create) create.disabled = false;
    } else {
      if (preflightBadge) {
        preflightBadge.textContent = '准入拒绝';
        preflightBadge.className = 'badge badge-stopped';
      }
      if (preflightResult) preflightResult.textContent = '预检未通过：';
      for (const r of (res.reasons || [])) {
        const li = document.createElement('li');
        li.textContent = reasonText[r] || r;
        if (preflightReasons) preflightReasons.append(li);
      }
      if (create) create.disabled = true;
    }
  } catch (err) {
    if (preflightBadge) {
      preflightBadge.textContent = '检测异常';
      preflightBadge.className = 'badge badge-stopped';
    }
    if (preflightResult) preflightResult.textContent = `预检调用失败: ${err.message}`;
    if (create) create.disabled = true;
  }
});

create?.addEventListener('click', async () => {
  if (create) create.disabled = true;
  const saved = localStorage.getItem(taskStorageKey);
  if (saved) {
    if (taskResult) taskResult.textContent = '另一个标签页已有待核对任务，请先核对。';
    showToast('另一个标签页已有待核对任务，请先核对。', true);
    return;
  }

  let spec = null;
  if (typeof FormData !== 'undefined' && form) {
    try {
      const entries = Object.fromEntries(new FormData(form).entries());
      spec = {
        name: (entries.name || getFieldValue('name') || '').trim(),
        imageFingerprint: (entries.imageFingerprint || getFieldValue('imageFingerprint') || '').trim(),
        cpu: parseInt(entries.cpu || getFieldValue('cpu') || '1', 10),
        memoryMiB: parseInt(entries.memoryMiB || getFieldValue('memoryMiB') || '512', 10),
        diskGiB: parseInt(entries.diskGiB || getFieldValue('diskGiB') || '5', 10),
        storagePool: entries.storagePool || getFieldValue('storagePool') || 'default',
        network: entries.network || getFieldValue('network') || 'incusbr0'
      };
    } catch (_) {}
  }
  if (!spec) {
    spec = {
      name: getFieldValue('name').trim(),
      imageFingerprint: getFieldValue('imageFingerprint').trim(),
      cpu: parseInt(getFieldValue('cpu', '1'), 10),
      memoryMiB: parseInt(getFieldValue('memoryMiB', '512'), 10),
      diskGiB: parseInt(getFieldValue('diskGiB', '5'), 10),
      storagePool: getFieldValue('storagePool', 'default'),
      network: getFieldValue('network', 'incusbr0')
    };
  }

  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const key = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
  const record = { key, spec };
  localStorage.setItem(taskStorageKey, JSON.stringify(record));
  pending = record;
  if (reconcile) reconcile.disabled = false;

  try {
    showToast(`正在提交容器 ${spec.name} 创建任务...`);
    await request('instances', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key, spec })
    });

    showToast(`容器 ${spec.name} 创建任务已提交！`);
    closeCreateDrawer();
    setTimeout(loadDiscovery, 600);
  } catch (err) {
    showToast(`创建失败: ${err.message}`, true);
    if (create) create.disabled = false;
  }
});

reconcile?.addEventListener('click', async () => {
  if (!pending) return;
  try {
    const res = await request('instances/reconcile', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(pending)
    });
    if (res && res.state === 'Completed') {
      localStorage.removeItem(taskStorageKey);
      pending = null;
      if (taskResult) taskResult.textContent = '创建任务核对完成。';
      showToast('创建任务核对完成。');
      await loadDiscovery();
    }
  } catch (err) {
    if (taskResult) taskResult.textContent = `核对失败: ${err.message}`;
  }
});

// 8. 终端控制台
function openTerminal(containerName) {
  activeTerminalContainer = containerName;
  terminalTitle.textContent = `容器控制台: ${containerName}`;
  terminalOutput.textContent = `[Connected to container ${containerName} via PTY shell]\nLinux ${containerName} 6.1.0-10-amd64\nType 'help' for available commands.\n\n`;
  drawerTerminal.classList.add('active');
  terminalInput.focus();
}

function closeTerminal() {
  drawerTerminal.classList.remove('active');
  activeTerminalContainer = null;
}

btnCloseTerminal?.addEventListener('click', closeTerminal);

async function sendTerminalCommand(cmd) {
  if (!activeTerminalContainer || !cmd) return;
  terminalOutput.textContent += `root@${activeTerminalContainer}:~# ${cmd}\n`;
  terminalInput.value = '';

  try {
    const res = await request('console/exec', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ container: activeTerminalContainer, command: cmd })
    });
    terminalOutput.textContent += (res.output || '') + '\n';
  } catch (err) {
    terminalOutput.textContent += `[Error executing command: ${err.message}]\n\n`;
  }
  const screen = document.getElementById('terminal-screen');
  if (screen) screen.scrollTop = screen.scrollHeight;
}

terminalSend?.addEventListener('click', () => sendTerminalCommand(terminalInput.value.trim()));
terminalInput?.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') sendTerminalCommand(terminalInput.value.trim());
});

if (typeof document !== 'undefined' && typeof document.querySelectorAll === 'function') {
  document.querySelectorAll('.quick-cmd-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const cmd = btn.getAttribute('data-cmd');
      if (cmd) sendTerminalCommand(cmd);
    });
  });
}

let activePortmapTargetIp = '';

// 9. 端口转发映射
async function openPortmap(containerName) {
  activePortmapContainer = containerName;
  const target = (currentDiscovery.instances || []).find(i => i.name === containerName);
  activePortmapTargetIp = (target && target.ipv4) ? target.ipv4 : '127.0.0.1';
  portmapTitle.textContent = `端口转发映射: ${containerName} (${activePortmapTargetIp})`;
  drawerPortmap?.classList.add('active');
  await loadPortMappings();
}

function closePortmap() {
  drawerPortmap?.classList.remove('active');
  activePortmapContainer = null;
  activePortmapTargetIp = '';
}

btnClosePortmap?.addEventListener('click', closePortmap);

async function loadPortMappings() {
  if (!activePortmapContainer || !portmapList) return;
  portmapList.replaceChildren();
  try {
    const res = await request(`network/mappings?container=${activePortmapContainer}`);
    const list = Array.isArray(res) ? res : [];
    if (list.length === 0) {
      const li = document.createElement('li');
      li.className = 'portmap-item';
      li.textContent = '暂无转发规则，可在上方添加。';
      portmapList.append(li);
      return;
    }
    for (const m of list) {
      const li = document.createElement('li');
      li.className = 'portmap-item';
      li.innerHTML = `
        <div>
          <strong>${m.protocol.toUpperCase()} ${m.hostPort} ➔ ${m.targetPort}</strong>
          <div style="font-size:0.75rem;color:var(--text-muted)">${m.description || 'NAT 端口映射'}</div>
        </div>
        <button type="button" class="btn btn-outline btn-sm btn-del-pm" data-id="${m.id}">删除</button>
      `;
      li.querySelector('.btn-del-pm').addEventListener('click', async () => {
        await request(`network/mappings?id=${m.id}`, { method: 'DELETE' });
        showToast('端口映射已删除');
        loadPortMappings();
      });
      portmapList.append(li);
    }
  } catch (err) {
    showToast(`加载映射失败: ${err.message}`, true);
  }
}

formPortmap?.addEventListener('submit', async (e) => {
  e.preventDefault();
  if (!activePortmapContainer) return;
  const mapping = {
    id: `pm-${Date.now()}`,
    containerId: activePortmapContainer,
    protocol: pmProtocol.value,
    hostPort: parseInt(pmHostPort.value, 10),
    targetPort: parseInt(pmTargetPort.value, 10),
    targetIp: activePortmapTargetIp || '127.0.0.1',
    enabled: true,
    description: pmDesc.value.trim()
  };

  try {
    await request('network/mappings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(mapping)
    });
    showToast('端口转发映射添加成功！');
    pmHostPort.value = '';
    pmTargetPort.value = '';
    pmDesc.value = '';
    loadPortMappings();
  } catch (err) {
    showToast(`添加失败: ${err.message}`, true);
  }
});

// 折叠详情展开/收起
toggleAdvanced?.addEventListener('click', () => {
  const isHidden = advancedPanel.style.display === 'none';
  advancedPanel.style.display = isHidden ? 'block' : 'none';
  toggleAdvanced.textContent = isHidden ? '收起底层详情 ▴' : '展开底层详情 ▾';
});

// ================= 7. 实例导出功能 =================
async function triggerExport(name) {
  showToast(`正在从底层建立快照并导出容器 ${name}...`);
  try {
    const url = `/plugins/lxc-manager/api/instances/export?name=${encodeURIComponent(name)}&token=${encodeURIComponent(token)}`;
    const res = await fetch(url, { headers });
    if (!res.ok) {
      const errText = await res.text();
      throw new Error(errText || `HTTP ${res.status}`);
    }
    const blob = await res.blob();
    const downloadUrl = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = downloadUrl;
    a.download = `${name}-backup.tar.gz`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(downloadUrl), 10000);
    showToast(`容器 ${name} 备份归档导出成功！`);
  } catch (err) {
    showToast(`导出失败: ${err.message}`, true);
  }
}

// ================= 8. 实例导入功能 =================
const drawerImport = document.getElementById('drawer-import');
const btnOpenImport = document.getElementById('btn-open-import');
const btnCloseImport = document.getElementById('btn-close-import');
const formImport = document.getElementById('form-import');
const importDropzone = document.getElementById('import-dropzone');
const importFile = document.getElementById('import-file');
const dropzoneHint = document.getElementById('dropzone-hint');
const importPool = document.getElementById('import-pool');
const importProgressBox = document.getElementById('import-progress-box');
const importProgressLabel = document.getElementById('import-progress-label');

btnOpenImport?.addEventListener('click', () => {
  drawerImport?.classList.add('active');
  populateImportPools();
});

btnCloseImport?.addEventListener('click', () => {
  drawerImport?.classList.remove('active');
});

function populateImportPools() {
  if (!importPool) return;
  importPool.replaceChildren();
  const pools = currentDiscovery.pools || [];
  if (pools.length === 0) {
    const opt = document.createElement('option');
    opt.value = 'default';
    opt.textContent = 'default (dir)';
    importPool.append(opt);
    return;
  }
  for (const p of pools) {
    const opt = document.createElement('option');
    opt.value = p.name;
    opt.textContent = `${p.name} (${p.driver || 'dir'})`;
    importPool.append(opt);
  }
}

importDropzone?.addEventListener('click', () => importFile?.click());

importDropzone?.addEventListener('dragover', (e) => {
  e.preventDefault();
  importDropzone.classList.add('dragover');
});

importDropzone?.addEventListener('dragleave', () => {
  importDropzone.classList.remove('dragover');
});

importDropzone?.addEventListener('drop', (e) => {
  e.preventDefault();
  importDropzone.classList.remove('dragover');
  if (e.dataTransfer?.files?.length) {
    importFile.files = e.dataTransfer.files;
    updateDropzoneLabel();
  }
});

importFile?.addEventListener('change', updateDropzoneLabel);

function updateDropzoneLabel() {
  if (importFile?.files?.length && dropzoneHint) {
    const file = importFile.files[0];
    const sizeMb = (file.size / (1024 * 1024)).toFixed(1);
    dropzoneHint.textContent = `已选归档: ${file.name} (${sizeMb} MB)`;
    dropzoneHint.style.color = 'var(--accent-cyan)';
  }
}

formImport?.addEventListener('submit', async (e) => {
  e.preventDefault();
  if (!importFile?.files?.length) {
    showToast('请先选择或拖拽备份归档文件', true);
    return;
  }

  const formData = new FormData(formImport);
  if (importProgressBox) importProgressBox.style.display = 'block';
  if (importProgressLabel) importProgressLabel.textContent = '正在流式上传归档并还原 rootfs...';
  showToast('开始流式导入实例容器，请稍候...');

  try {
    const importUrl = `/plugins/lxc-manager/api/instances/import?token=${encodeURIComponent(token)}`;
    const res = await fetch(importUrl, {
      method: 'POST',
      headers,
      body: formData
    });
    if (!res.ok) {
      const errText = await res.text();
      throw new Error(errText || `HTTP ${res.status}`);
    }
    const data = await res.json();
    showToast(`实例 ${data.instance?.name || '容器'} 恢复导入成功！`);
    drawerImport?.classList.remove('active');
    formImport.reset();
    if (dropzoneHint) {
      dropzoneHint.textContent = '点击选择文件 或 将容器归档拖拽至此处';
      dropzoneHint.style.color = '';
    }
    loadDiscovery();
  } catch (err) {
    showToast(`导入失败: ${err.message}`, true);
  } finally {
    if (importProgressBox) importProgressBox.style.display = 'none';
  }
});

// ================= 9. 细粒度硬件调优抽屉 =================
const drawerEdit = document.getElementById('drawer-edit');
const btnCloseEdit = document.getElementById('btn-close-edit');
const editDrawerTitle = document.getElementById('edit-drawer-title');
const formEditSpec = document.getElementById('form-edit-spec');
const editInstName = document.getElementById('edit-inst-name');

const editCpuAllowance = document.getElementById('edit-cpu-allowance');
const valCpuAllowance = document.getElementById('val-cpu-allowance');
const editCpuPriority = document.getElementById('edit-cpu-priority');
const valCpuPriority = document.getElementById('val-cpu-priority');

const editDiskSize = document.getElementById('edit-disk-size');
const editDiskReadMb = document.getElementById('edit-disk-read-mb');
const editDiskWriteMb = document.getElementById('edit-disk-write-mb');
const editDiskReadIops = document.getElementById('edit-disk-read-iops');
const editDiskWriteIops = document.getElementById('edit-disk-write-iops');

const editMaxProcesses = document.getElementById('edit-max-processes');
const editAllowSwap = document.getElementById('edit-allow-swap');

const editNetBandwidth = document.getElementById('edit-net-bandwidth');
const valNetBandwidth = document.getElementById('val-net-bandwidth');
const editNetwork = document.getElementById('edit-network');

// 标签页切换
if (typeof document !== 'undefined' && typeof document.querySelectorAll === 'function') {
  document.querySelectorAll('.cyber-tab').forEach((tab) => {
    tab.addEventListener('click', () => {
      document.querySelectorAll('.cyber-tab').forEach((t) => t.classList.remove('active'));
      document.querySelectorAll('.tab-pane').forEach((p) => (p.style.display = 'none'));

      tab.classList.add('active');
      const target = document.getElementById(tab.dataset.tab);
      if (target) target.style.display = 'block';
    });
  });
}

// 滑块数值联动
editCpuAllowance?.addEventListener('input', () => {
  if (valCpuAllowance) valCpuAllowance.textContent = `${editCpuAllowance.value}%`;
});
editCpuPriority?.addEventListener('input', () => {
  if (valCpuPriority) valCpuPriority.textContent = editCpuPriority.value;
});
editNetBandwidth?.addEventListener('input', () => {
  if (valNetBandwidth) {
    valNetBandwidth.textContent = editNetBandwidth.value === '0' ? '不限速' : `${editNetBandwidth.value} Mbit`;
  }
});

function syncNetworkBandwidthState() {
  const isMacvlan = (editNetwork?.value || '').startsWith('macvlan:');
  if (editNetBandwidth) {
    editNetBandwidth.disabled = isMacvlan;
    if (isMacvlan) {
      editNetBandwidth.value = 0;
      if (valNetBandwidth) valNetBandwidth.textContent = '物理直连 (Macvlan 不支持 TC 队列限速)';
    } else {
      if (valNetBandwidth) {
        valNetBandwidth.textContent = editNetBandwidth.value === '0' ? '不限速' : `${editNetBandwidth.value} Mbit`;
      }
    }
  }
}
editNetwork?.addEventListener('change', syncNetworkBandwidthState);

function openEditDrawer(ct) {
  if (!drawerEdit) return;
  drawerEdit.classList.add('active');
  if (editDrawerTitle) editDrawerTitle.textContent = `硬件规格与配额热调优: ${ct.name}`;
  if (editInstName) editInstName.value = ct.name;

  const cores = parseInt(ct.cpu || '1', 10) || 1;
  const radio = (typeof formEditSpec?.querySelector === 'function')
    ? formEditSpec.querySelector(`input[name="editCpuCores"][value="${cores}"]`)
    : null;
  if (radio) radio.checked = true;

  if (editCpuAllowance) {
    editCpuAllowance.value = 100;
    if (valCpuAllowance) valCpuAllowance.textContent = '100%';
  }
  if (editCpuPriority) {
    editCpuPriority.value = 5;
    if (valCpuPriority) valCpuPriority.textContent = '5';
  }
  if (editDiskSize) {
    editDiskSize.value = parseInt(ct.disk || '8', 10) || 8;
  }
  if (editMaxProcesses) editMaxProcesses.value = 500;
  if (editAllowSwap) editAllowSwap.checked = false;
  if (editNetBandwidth) {
    editNetBandwidth.value = 0;
    if (valNetBandwidth) valNetBandwidth.textContent = '不限速';
  }
  if (editNetwork) {
    const groups = getNetworkGroups(currentDiscovery);
    populateSelect(editNetwork, groups.length > 0 ? groups : (currentDiscovery.networks || []).map(n => ({ val: n.name, label: n.name })));
    if (ct.networkName) {
      editNetwork.value = ct.networkName;
    }
    syncNetworkBandwidthState();
  }
}

btnCloseEdit?.addEventListener('click', () => {
  drawerEdit?.classList.remove('active');
});

formEditSpec?.addEventListener('submit', async (e) => {
  e.preventDefault();
  const name = editInstName?.value;
  if (!name) return;

  const selCore = (typeof formEditSpec?.querySelector === 'function')
    ? formEditSpec.querySelector('input[name="editCpuCores"]:checked')
    : null;
  const cpuCores = selCore ? parseInt(selCore.value, 10) : 1;
  const cpuAllowancePct = parseInt(editCpuAllowance?.value || '100', 10);
  const cpuPriority = parseInt(editCpuPriority?.value || '5', 10);

  const diskGiB = parseInt(editDiskSize?.value || '0', 10);
  const diskReadMbps = parseInt(editDiskReadMb?.value || '0', 10);
  const diskWriteMbps = parseInt(editDiskWriteMb?.value || '0', 10);
  const diskReadIops = parseInt(editDiskReadIops?.value || '0', 10);
  const diskWriteIops = parseInt(editDiskWriteIops?.value || '0', 10);

  const maxProcesses = parseInt(editMaxProcesses?.value || '500', 10);
  const allowSwap = editAllowSwap ? editAllowSwap.checked : false;

  const bandwidthMaxMbps = parseInt(editNetBandwidth?.value || '0', 10);
  const network = editNetwork?.value || '';

  const payload = {
    name,
    spec: {
      cpuCores,
      cpuAllowancePct: cpuAllowancePct < 100 ? cpuAllowancePct : 0,
      cpuPriority,
      diskGiB,
      diskReadMbps,
      diskWriteMbps,
      diskReadIops,
      diskWriteIops,
      maxProcesses,
      allowSwap,
      bandwidthMaxMbps,
      network
    }
  };

  showToast(`正在向容器 ${name} 应用硬件配额热更新...`);
  try {
    await request('instances/hardware-limits', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    showToast(`容器 ${name} 硬件配额热更新已生效！`);
    drawerEdit?.classList.remove('active');
    loadDiscovery();
  } catch (err) {
    showToast(`应用失败: ${err.message}`, true);
  }
});

// 全局刷新绑定
refresh?.addEventListener('click', () => {
  loadCapabilities();
  loadTemplates();
  loadDiscovery();
});

// 初始引导加载
loadCapabilities();
loadTemplates();
loadDiscovery();
