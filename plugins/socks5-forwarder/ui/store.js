// store.js - 轻量原生响应式状态中心与通信管道 (Reactive Store & EventBus)

class EventEmitter {
  constructor() {
    this.events = {};
  }
  on(event, listener) {
    if (!this.events[event]) this.events[event] = [];
    this.events[event].push(listener);
    return () => this.off(event, listener);
  }
  off(event, listener) {
    if (!this.events[event]) return;
    this.events[event] = this.events[event].filter(l => l !== listener);
  }
  emit(event, ...args) {
    if (!this.events[event]) return;
    this.events[event].forEach(l => {
      try { l(...args); } catch (e) { console.error(`Event ${event} error:`, e); }
    });
  }
}

export const eventBus = new EventEmitter();

// 核心全局响应式状态
export const store = {
  activeTab: 'topology', // topology | selectors | nodes | connections
  upstreams: [],
  inbounds: [],
  pipelines: [],
  chains: [],
  connections: [],
  tunStatus: null,
  loading: false,
  autoRefreshInterval: null,
  
  // 快速读取辅助
  getUpstreamById(id) {
    return this.upstreams.find(u => u.id === id);
  },
  getPipelineById(id) {
    return this.pipelines.find(p => p.id === id);
  },
  getChainById(id) {
    return this.chains.find(c => c.id === id);
  }
};

// 统一下层 API 通信适配器
export async function apiCall(endpoint, options = {}) {
  const url = `./api/${endpoint.replace(/^\//, '')}`;
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const fetchOpts = { ...options, headers };
  if (options.body && typeof options.body === 'object') {
    fetchOpts.body = JSON.stringify(options.body);
  }

  try {
    const res = await fetch(url, fetchOpts);
    if (!res.ok) {
      let errText = await res.text();
      try {
        const jsonErr = JSON.parse(errText);
        errText = jsonErr.error || jsonErr.message || errText;
      } catch (_) {}
      throw new Error(errText || `HTTP ${res.status}`);
    }
    const contentType = res.headers.get('content-type') || '';
    if (contentType.includes('application/json')) {
      return await res.json();
    }
    return await res.text();
  } catch (err) {
    console.error(`[API Error] ${endpoint}:`, err);
    throw err;
  }
}

// 统一刷新所有数据
export async function refreshAllData() {
  store.loading = true;
  eventBus.emit('loading', true);
  try {
    const data = await apiCall('profiles');
    store.upstreams = data.upstreams || [];
    store.inbounds = data.inbounds || [];
    store.pipelines = data.pipelines || [];
    store.chains = data.chains || [];

    // 探查 TUN 网卡状态
    try {
      store.tunStatus = await apiCall('system/tun-status');
    } catch (_) {}

    eventBus.emit('data-refreshed', store);
  } catch (err) {
    eventBus.emit('toast', { type: 'error', message: '刷新数据失败: ' + err.message });
  } finally {
    store.loading = false;
    eventBus.emit('loading', false);
  }
}

// 极速出站切换 API
export async function switchPipelineOutbound(pipelineId, outboundId) {
  try {
    const res = await apiCall('selectors/switch', {
      method: 'POST',
      body: { pipelineId, outboundId }
    });
    await refreshAllData();
    const targetName = outboundId === 'direct' ? '本地直连' : (store.getUpstreamById(outboundId)?.name || outboundId);
    eventBus.emit('toast', { type: 'success', message: `线路已切换至: ${targetName}` });
    return res;
  } catch (err) {
    eventBus.emit('toast', { type: 'error', message: '切网失败: ' + err.message });
    throw err;
  }
}

// 全局并发测速 API
export async function pingAllNodes() {
  eventBus.emit('toast', { type: 'info', message: '⚡ 正在并发测速所有节点...' });
  try {
    const res = await apiCall('upstreams/ping-all', { method: 'POST' });
    await refreshAllData();
    eventBus.emit('toast', { type: 'success', message: '全量测速完成！' });
    return res.results || [];
  } catch (err) {
    eventBus.emit('toast', { type: 'error', message: '测速失败: ' + err.message });
    throw err;
  }
}
