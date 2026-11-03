/**
 * LinuxWebTool 插件 URL 解析与 UI 模式决策策略机 (PluginUrlResolver)
 * 单一事实源 (Single Source of Truth)，统一治理插件中心各组件的 URL 拼装、降级回退与外链跳转逻辑。
 */

import { API } from '../config.js';

export const PluginUrlResolver = {
  /**
   * 判定插件是否具备 Webview 视图控制台 (模式 3)
   * @param {Object} plugin
   * @returns {boolean}
   */
  hasWebview(plugin) {
    if (!plugin || !plugin.manifest) return false;
    const ui = plugin.manifest.ui;
    return Boolean(ui && (ui.entry || ui.entryFile || ui.hasWebview));
  },

  /**
   * 判定插件是否具备面向普通浏览器的独立 HTTP Web/API 接口 (模式 2)
   * 严格过滤纯 TCP/SOCKS5 等底层代理端口，杜绝无 Web 站点的插件误渲染“访问网页”按钮
   * @param {Object} plugin
   * @returns {boolean}
   */
  hasIndependentHttpGateway(plugin) {
    if (!plugin || !plugin.manifest) return false;
    const isResident = plugin.profile === 0;
    if (!isResident) return false;

    const manifest = plugin.manifest;
    const net = manifest.network || {};
    const ui = manifest.ui || {};

    // 1. 若显式声明 ui.entry === 'web'，代表该插件以全功能 Web 页面为主界面
    if (ui.entry === 'web') return true;

    // 2. 若网络模式为 path 路径反代模式，代表挂载在宿主 HTTP 路由下
    if (net.mode === 'path') return true;

    // 3. 检查是否有明确的 HTTP 反向代理 Ingress 规则 (排除纯 WebSocket 双工流或非 HTTP 代理)
    const ingress = manifest.ingress;
    if (ingress && ingress.enabled && Array.isArray(ingress.routes)) {
      const hasHttpRoute = ingress.routes.some(r => {
        const mode = r.transport?.mode?.toLowerCase();
        return mode === 'http_reverse_proxy';
      });
      if (hasHttpRoute) return true;
    }

    // 4. 若插件已拥有专门的静态 Webview 控制台 (如 socks5-proxy 的 ui/index.html)，
    // 且没有独立的 HTTP 反向代理路由，则该插件的主入口为 Webview，不呈现冗余的独立网页按钮
    if (this.hasWebview(plugin)) {
      return false;
    }

    // 5. 其他声明了 supportHttp 且未被 Webview 占用的微服务
    return Boolean(net.supportHttp);
  },

  /**
   * 获取插件 Webview 静态资产 URL
   * @param {Object} plugin
   * @param {string} [token]
   * @returns {string}
   */
  getWebviewUrl(plugin, token = '') {
    if (!plugin || !plugin.id) return '';
    let url = '';
    const rawEntry = plugin.manifest?.ui?.entry || plugin.manifest?.ui?.entryFile;

    if (rawEntry && rawEntry !== 'web') {
      const cleanEntry = rawEntry.replace(/^\/+/, '').replace(/^ui\//, '');
      url = API.plugins.asset(plugin.id, cleanEntry);
    } else if (plugin.manifest?.ui?.entry === 'web' || plugin.gatewayUrl || plugin.manifest?.network?.mode === 'path') {
      url = plugin.gatewayUrl || API.plugins.gatewayUrl(plugin.id);
    } else {
      url = API.plugins.asset(plugin.id, 'index.html');
    }

    if (url && token) {
      const sep = url.includes('?') ? '&' : '?';
      return `${url}${sep}token=${encodeURIComponent(token)}`;
    }
    return url;
  },

  /**
   * 获取插件独立 HTTP 网关直达 URL
   * 自动降级回退至 /plugins/{id}/，杜绝 gatewayUrl 为空时的静默失败
   * @param {Object} plugin
   * @param {string} [token]
   * @returns {string}
   */
  getGatewayUrl(plugin, token = '') {
    if (!plugin || !plugin.id) return '';

    // 优先读取下发的 gatewayUrl，若无则自动调用 API.plugins.gatewayUrl(id) 兜底
    let url = plugin.gatewayUrl || API.plugins.gatewayUrl(plugin.id);

    if (url && token) {
      const sep = url.includes('?') ? '&' : '?';
      return `${url}${sep}token=${encodeURIComponent(token)}`;
    }
    return url;
  },

  /**
   * 智能打开插件的主操作页面 (具有完备错误拦截与 Toast 兜底)
   * @param {Object} plugin
   * @param {string} [token]
   * @param {Object} [toast]
   * @returns {boolean} 是否成功打开
   */
  openPluginGateway(plugin, token = '', toast = null) {
    if (!plugin) return false;

    let targetUrl = '';
    if (this.hasIndependentHttpGateway(plugin)) {
      targetUrl = this.getGatewayUrl(plugin, token);
    } else if (this.hasWebview(plugin)) {
      targetUrl = this.getWebviewUrl(plugin, token);
    }

    if (targetUrl) {
      window.open(targetUrl, '_blank');
      return true;
    }

    if (toast && typeof toast.warning === 'function') {
      toast.warning(`微服务 [${plugin.name || plugin.id}] 未提供可直接访问的 Web 页面接口`);
    } else {
      console.warn(`[PluginUrlResolver] Plugin ${plugin.id} has no accessible web endpoint.`);
    }
    return false;
  }
};
