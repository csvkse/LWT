const token = new URLSearchParams(location.search).get('token') || '';
const refresh = document.getElementById('refresh');
const status = document.getElementById('status');
const error = document.getElementById('error');
const lists = ['instances', 'images', 'pools', 'networks'];
const form = document.getElementById('preflight');
const check = document.getElementById('check');
const preflightResult = document.getElementById('preflight-result');
const preflightReasons = document.getElementById('preflight-reasons');
const sample = document.getElementById('sample');
const sampleResult = document.getElementById('sample-result');
const create = document.getElementById('create');
const reconcile = document.getElementById('reconcile');
const taskResult = document.getElementById('task-result');
const managedList = document.getElementById('managed');
const managedError = document.getElementById('managed-error');
const serverTasksList = document.getElementById('server-tasks');
const serverTasksError = document.getElementById('server-tasks-error');
const taskStorageKey = 'lwt.lxc-manager.pending-create.v1';
let pending = null;
let managed = [];
let serverTasks = [];
let taskInventoryReady = false;
let approvedSpec = null;
let revision = 0;
try {
  const saved = localStorage.getItem(taskStorageKey);
  if (saved) {
    const value = JSON.parse(saved);
    if (validTask(value)) pending = value;
    else throw new Error('invalid task');
  }
} catch {
  taskResult.textContent = '无法读取本地任务记录，请检查浏览器存储后再操作。';
  pending = { invalid: true };
}
if (pending && !pending.invalid) { reconcile.disabled = false; taskResult.textContent = pending.kind === 'lifecycle' ? '存在尚未核对的操作任务。' : '存在尚未核对的创建任务。'; }
const headers = token ? { Authorization: `Bearer ${token}` } : {};
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

function validTask(value) {
  return value && /^[a-f0-9]{32}$/.test(value.key) && value.spec && typeof value.spec === 'object' &&
    (value.kind === undefined || (value.kind === 'lifecycle' && /^[a-f0-9]{32}$/.test(value.owner) && ['start','stop','restart'].includes(value.action)));
}

function taskPayload(value) {
  return value.kind === 'lifecycle' ? { key:value.key, owner:value.owner, spec:value.spec, action:value.action } : { key:value.key, spec:value.spec };
}

function renderManaged() {
  managedList.replaceChildren();
  if (!managed.length) { const li=document.createElement('li'); li.textContent='暂无已托管实例'; managedList.append(li); return; }
  for (const item of managed) {
    const li=document.createElement('li');
    const label=document.createElement('span'); label.textContent=`${item.spec.name} · ${item.verified ? '归属已核对' : '归属或配置不匹配'}`; li.append(label);
    for (const [action,text] of [['start','启动'],['stop','停止'],['restart','重启']]) {
      const button=document.createElement('button'); button.type='button'; button.textContent=`${text} ${item.spec.name}`;
      button.disabled=!item.verified || Boolean(pending) || !taskInventoryReady || serverTasks.length > 0;
      button.addEventListener('click',()=>submitLifecycle(item,action)); li.append(button);
    }
    managedList.append(li);
  }
}

function serverTaskRecord(task) {
  if (!task.details || typeof task.details !== 'object') return null;
  const value=task.details.owner ? {kind:'lifecycle',key:task.key,owner:task.details.owner,spec:task.details.spec,action:task.details.action} : {key:task.key,spec:task.details};
  return validTask(value) && typeof value.spec.name === 'string' && value.spec.name ? value : null;
}

function renderServerTasks() {
  serverTasksList.replaceChildren();
  if (!serverTasks.length) { const li=document.createElement('li'); li.textContent='暂无待核对任务'; serverTasksList.append(li); return; }
  for (const task of serverTasks) {
    const record=serverTaskRecord(task); const li=document.createElement('li'); const label=document.createElement('span');
    label.textContent=record ? `${record.spec.name} · ${task.state}` : `任务 ${task.key} · 信息不完整，请人工核对`;
    li.append(label);
    if (record) { const button=document.createElement('button');button.type='button';button.textContent=`恢复核对 ${record.spec.name}`;button.disabled=Boolean(pending);button.addEventListener('click',()=>restoreTask(record));li.append(button); }
    serverTasksList.append(li);
  }
}

async function loadTasks() {
  taskInventoryReady=false; serverTasksError.textContent='';
  try { const tasks=await request('tasks');if(!Array.isArray(tasks))throw new Error('invalid task list');serverTasks=tasks;taskInventoryReady=true; }
  catch { serverTasks=[];serverTasksError.textContent='无法读取服务端任务，已暂停新的操作。'; }
  renderServerTasks();renderManaged();
}

async function restoreTask(record) {
  if (pending || !validTask(record)) return;
  try {
    if (!navigator.locks?.request) {taskResult.textContent='恢复任务需要支持 Web Locks 的安全上下文（HTTPS 或 localhost）。';return;}
    await navigator.locks.request(taskStorageKey,async()=>{
      if (localStorage.getItem(taskStorageKey)) {taskResult.textContent='已有本地待核对任务，请先处理。';return;}
      localStorage.setItem(taskStorageKey,JSON.stringify(record));pending=record;approvedSpec=null;create.disabled=true;reconcile.disabled=false;renderManaged();renderServerTasks();
      taskResult.textContent='已恢复任务，请核对。';
    });
  } catch {taskResult.textContent='恢复任务失败，请检查浏览器存储。';}
}

async function request(path, options = {}) {
  const response = await fetch(`/plugins/lxc-manager/api/${path}`, { ...options, headers: { ...headers, ...options.headers }, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    const failure=new Error(`请求失败（HTTP ${response.status}）`);
    if (response.status===409) {try {const body=await response.json();if(body.code==='not_submitted')failure.code=body.code;} catch { /* unknown outcome */ }}
    throw failure;
  }
  return response.json();
}

// Called while holding the browser task lock. Only the explicit server result
// for a pre-submission rejection permits discarding the local task record.
async function submitRecorded(path,record) {
  try {
    const result=await request(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(taskPayload(record))});
    taskResult.textContent=`任务状态：${result.state}。请核对任务。`;
  } catch (e) {
    if(e.code!=='not_submitted')throw e;
    const saved=JSON.parse(localStorage.getItem(taskStorageKey)||'null');
    if(JSON.stringify(saved)!==JSON.stringify(record))throw new Error('task changed');
    localStorage.removeItem(taskStorageKey);pending=null;reconcile.disabled=true;
    taskResult.textContent=record.kind==='lifecycle'?'服务端已明确拒绝，未提交操作任务。':'服务端已明确拒绝，未提交创建任务。';
    renderManaged();renderServerTasks();await loadTasks();
  }
}

function setOptions(name, items, value, label) {
  const select = form.elements.namedItem(name);
  select.replaceChildren();
  for (const item of items) {
    const option = document.createElement('option');
    option.value = value(item);
    option.textContent = label(item);
    select.append(option);
  }
}

function renderList(id, values, describe) {
  const list = document.getElementById(id);
  list.replaceChildren();
  for (const value of values) {
    const item = document.createElement('li');
    item.textContent = describe(value);
    list.append(item);
  }
  if (!values.length) {
    const item = document.createElement('li');
    item.textContent = '暂无';
    list.append(item);
  }
}

async function load() {
  revision++;
  approvedSpec = null;
  create.disabled = true;
  refresh.disabled = true;
  error.textContent = '';
  check.disabled = true;
  sample.disabled = true;
  sampleResult.textContent = '';
  preflightResult.textContent = '';
  preflightReasons.replaceChildren();
  status.textContent = '正在读取 Incus 状态…';
  for (const id of lists) document.getElementById(id).replaceChildren();
  managed=[]; managedError.textContent=''; renderManaged();
  await loadTasks();
  try {
    const capability = await request('capabilities');
    if (capability.status !== 'Ready') {
      status.textContent = `Incus 状态：${capability.status}。请检查服务、socket 和访问权限。`;
      return;
    }
    status.textContent = `Incus 已连接（${capability.version || '版本未知'}）；正在读取资源…`;
    const data = await request('discovery');
    try { const items = await request('managed'); if (!Array.isArray(items)) throw new Error('invalid inventory'); managed = items; renderManaged(); } catch { managed=[]; renderManaged(); managedError.textContent = '无法读取已托管实例。'; }
    renderList('instances', data.instances, x => `${x.name} · ${x.status}`);
    renderList('images', data.images, x => `${x.description || x.fingerprint} · ${x.architecture}`);
    renderList('pools', data.pools, x => `${x.name} · ${x.driver} · ${x.status}`);
    renderList('networks', data.networks, x => x.name);
    setOptions('imageFingerprint', data.images, x => x.fingerprint, x => `${x.description || x.fingerprint} · ${x.architecture}`);
    setOptions('storagePool', data.pools.filter(x => x.status === 'Created'), x => x.name, x => `${x.name} · ${x.driver}`);
    setOptions('network', data.networks, x => x.name, x => x.name);
    check.disabled = !form.elements.imageFingerprint.options.length || !form.elements.storagePool.options.length || !form.elements.network.options.length;
    sample.disabled = !form.elements.storagePool.options.length;
    status.textContent = `Incus 已连接（${capability.version || '版本未知'}）。资源配置和硬配额尚未验证。`;
  } catch (e) {
    status.textContent = '无法读取状态';
    error.textContent = e instanceof Error ? e.message : '未知错误';
  } finally {
    refresh.disabled = false;
  }
}
sample.addEventListener('click', async () => {
  sample.disabled = true;
  sampleResult.textContent = '正在读取资源…';
  try {
    const pool = form.elements.storagePool.value;
    const data = await request(`resources?pool=${encodeURIComponent(pool)}`);
    const gib = n => (n / (1024 ** 3)).toFixed(1);
    sampleResult.textContent = `CPU ${data.cpuThreads} 线程；内存已用 ${gib(data.memoryUsedBytes)} / ${gib(data.memoryTotalBytes)} GiB；存储池已用 ${gib(data.poolUsedBytes)} / ${gib(data.poolTotalBytes)} GiB。此采样不代表可分配额度或硬配额已生效。`;
  } catch (e) {
    sampleResult.textContent = e instanceof Error ? e.message : '读取资源失败';
  } finally {
    sample.disabled = false;
  }
});
form.addEventListener('submit', async event => {
  event.preventDefault();
  if (!form.reportValidity()) return;
  check.disabled = true;
  create.disabled = true;
  approvedSpec = null;
  const requestedRevision = ++revision;
  preflightReasons.replaceChildren();
  preflightResult.textContent = '正在检查…';
  try {
    const values = new FormData(form);
    const spec = Object.fromEntries(values.entries());
    for (const key of ['cpu', 'memoryMiB', 'diskGiB']) spec[key] = Number(spec[key]);
    const result = await request('preflight', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(spec) });
    if (requestedRevision !== revision) { preflightResult.textContent = '配置已变更，请重新检查。'; return; }
    preflightResult.textContent = !result.allowed ? '预检未通过：' : (!taskInventoryReady ? '预检通过，但无法读取服务端任务。' : (pending || serverTasks.length ? '预检通过，请先核对待处理任务。' : '预检通过，可提交创建。'));
    if (result.allowed && !pending && taskInventoryReady && serverTasks.length===0) { approvedSpec = spec; create.disabled = false; }
    renderList('preflight-reasons', result.reasons, x => reasonText[x] || x);
  } catch (e) {
    preflightResult.textContent = e instanceof Error ? e.message : '预检失败';
  } finally {
    check.disabled = false;
  }
});
for (const event of ['input', 'change']) form.addEventListener(event, () => {
  revision++;
  approvedSpec = null;
  create.disabled = true;
});
create.addEventListener('click', async () => {
  if (!approvedSpec || pending || !taskInventoryReady || serverTasks.length>0 || !form.reportValidity()) return;
  const current = Object.fromEntries(new FormData(form).entries());
  for (const key of ['cpu', 'memoryMiB', 'diskGiB']) current[key] = Number(current[key]);
  if (JSON.stringify(current) !== JSON.stringify(approvedSpec)) { approvedSpec = null; create.disabled = true; preflightResult.textContent = '配置已变更，请重新检查。'; return; }
  const creationSpec = approvedSpec;
  const creationRevision = revision;
  create.disabled = true;
  try {
    if (!navigator.locks?.request) { taskResult.textContent = '创建需要支持 Web Locks 的安全上下文（HTTPS 或 localhost）。'; return; }
    await navigator.locks.request(taskStorageKey, async () => {
      if (creationRevision !== revision) { taskResult.textContent = '配置已变更，请重新检查。'; return; }
      const saved = localStorage.getItem(taskStorageKey);
      if (saved) {
        const value = JSON.parse(saved);
        if (!validTask(value)) throw new Error('invalid task');
        pending = value;
        renderManaged();
        reconcile.disabled = false;
        taskResult.textContent = '另一个标签页已有待核对任务，请先核对。';
        return;
      }
      const bytes = new Uint8Array(16); crypto.getRandomValues(bytes);
      const key = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
      const record = { key, spec: creationSpec };
      localStorage.setItem(taskStorageKey, JSON.stringify(record));
      pending = record;
      renderManaged();
      approvedSpec = null;
      reconcile.disabled = false;
      taskResult.textContent = '正在提交创建任务…';
      await submitRecorded('instances',record);
    });
  } catch (e) {
    taskResult.textContent = pending ? '提交结果未确认，请核对任务；不要重新提交。' : '无法保存任务记录，未提交创建。';
  }
});
async function submitLifecycle(item,action) {
  if (!item.verified || pending || !taskInventoryReady || serverTasks.length>0) return;
  try {
    if (!navigator.locks?.request) { taskResult.textContent='操作需要支持 Web Locks 的安全上下文（HTTPS 或 localhost）。'; return; }
    await navigator.locks.request(taskStorageKey,async()=>{
      const saved=localStorage.getItem(taskStorageKey);
      if (saved) {
        const value=JSON.parse(saved); if (!validTask(value)) throw new Error('invalid task');
        pending=value; reconcile.disabled=false; renderManaged(); taskResult.textContent='另一个标签页已有待核对任务，请先核对。'; return;
      }
      const bytes=new Uint8Array(16);crypto.getRandomValues(bytes);
      const key=Array.from(bytes,value=>value.toString(16).padStart(2,'0')).join('');
      const record={kind:'lifecycle',key,owner:item.key,spec:item.spec,action};
      localStorage.setItem(taskStorageKey,JSON.stringify(record));pending=record;approvedSpec=null;create.disabled=true;reconcile.disabled=false;renderManaged();
      taskResult.textContent='正在提交操作任务…';
      await submitRecorded('lifecycle',record);
    });
  } catch { taskResult.textContent=pending?'提交结果未确认，请核对任务；不要重新提交。':'无法保存任务记录，未提交操作。'; }
}
reconcile.addEventListener('click', async () => {
  if (!pending || pending.invalid) return;
  reconcile.disabled = true;
  try {
    if (!navigator.locks?.request) { taskResult.textContent = '核对需要支持 Web Locks 的安全上下文（HTTPS 或 localhost）。'; return; }
    await navigator.locks.request(taskStorageKey, async () => {
      const saved = JSON.parse(localStorage.getItem(taskStorageKey) || 'null');
      if (!validTask(saved) || JSON.stringify(saved) !== JSON.stringify(pending)) throw new Error('task changed');
      const isLifecycle=pending.kind==='lifecycle';
      const result = await request(isLifecycle ? 'lifecycle/reconcile' : 'instances/reconcile', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(taskPayload(pending)) });
      if (result.state === 'Completed') {
        localStorage.removeItem(taskStorageKey);
        pending = null;
        taskResult.textContent = isLifecycle ? '操作任务核对完成。' : '创建任务核对完成。';
        renderManaged();
        await loadTasks();
      } else taskResult.textContent = `任务状态：${result.state}。如结果不确定，请检查后端状态。`;
    });
  } catch { taskResult.textContent = '核对失败，请稍后重试；任务记录已保留。'; }
  finally { reconcile.disabled = !pending || pending.invalid; }
});
refresh.addEventListener('click', load);
load();
