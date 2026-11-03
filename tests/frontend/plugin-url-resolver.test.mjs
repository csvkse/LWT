import assert from 'node:assert/strict';
import { test } from 'node:test';

import { PluginUrlResolver } from '../../src/LinuxWebTool.WebHost/wwwroot/app/utils/pluginUrlResolver.js';

test('PLUGIN-URL-01: PluginUrlResolver.hasWebview 正确识别 Webview 扩展配置', () => {
  const pluginWithEntry = {
    id: 'socks5-proxy',
    manifest: {
      ui: { entry: 'ui/index.html' }
    }
  };
  assert.equal(PluginUrlResolver.hasWebview(pluginWithEntry), true);

  const pluginWithFlag = {
    id: 'custom-view',
    manifest: {
      ui: { hasWebview: true }
    }
  };
  assert.equal(PluginUrlResolver.hasWebview(pluginWithFlag), true);

  const pluginWithoutUi = {
    id: 'headless-worker',
    manifest: {}
  };
  assert.equal(PluginUrlResolver.hasWebview(pluginWithoutUi), false);
});

test('PLUGIN-URL-02: hasIndependentHttpGateway 精准区分纯 SOCKS5 代理与独立 Web 站点', () => {
  // 核心回归用例：socks5-proxy 具有本地端口与静态 Webview，但无独立 HTTP Web 接口
  // 必须判定为 false，消除卡片上的坏死冗余按钮
  const socksPlugin = {
    id: 'socks5-proxy',
    profile: 0,
    manifest: {
      network: {
        mode: 'port',
        port: 18080,
        supportHttp: true // 后端泛化计算字段
      },
      ui: {
        entry: 'ui/index.html'
      },
      ingress: {
        enabled: true,
        routes: [
          { transport: { mode: 'duplex_stream' } } // 纯全双工流，非 HTTP 反代
        ]
      }
    }
  };
  assert.equal(PluginUrlResolver.hasIndependentHttpGateway(socksPlugin), false);

  // 真正的 Web 站点微服务 (ui.entry === 'web')
  const webSitePlugin = {
    id: 'file-browser',
    profile: 0,
    manifest: {
      ui: { entry: 'web' },
      network: { supportHttp: true }
    }
  };
  assert.equal(PluginUrlResolver.hasIndependentHttpGateway(webSitePlugin), true);

  // 具有 HTTP 反向代理 Ingress 路由的微服务
  const reverseProxyPlugin = {
    id: 'caddy-dashboard',
    profile: 0,
    manifest: {
      ingress: {
        enabled: true,
        routes: [
          { transport: { mode: 'http_reverse_proxy' } }
        ]
      }
    }
  };
  assert.equal(PluginUrlResolver.hasIndependentHttpGateway(reverseProxyPlugin), true);
});

test('PLUGIN-URL-03: getGatewayUrl 在 gatewayUrl 为 undefined 时成功兜底到 /plugins/{id}/', () => {
  const pluginWithoutGatewayUrl = {
    id: 'my-service',
    // gatewayUrl 未定义
  };

  const url = PluginUrlResolver.getGatewayUrl(pluginWithoutGatewayUrl, 'test_token_123');
  assert.equal(url, '/plugins/my-service/?token=test_token_123');

  // 当显式提供 gatewayUrl 时，优先尊重指定 URL
  const pluginWithCustomGateway = {
    id: 'my-service',
    gatewayUrl: '/custom/gateway/path'
  };
  const customUrl = PluginUrlResolver.getGatewayUrl(pluginWithCustomGateway, 'test_token_123');
  assert.equal(customUrl, '/custom/gateway/path?token=test_token_123');
});

test('PLUGIN-URL-04: getWebviewUrl 规范化生成静态资产地址并注入 Token', () => {
  const plugin = {
    id: 'socks5-proxy',
    manifest: {
      ui: { entry: 'ui/index.html' }
    }
  };

  const url = PluginUrlResolver.getWebviewUrl(plugin, 'secret_token_abc');
  assert.equal(url, '/api/Plugins/socks5-proxy/Assets/index.html?token=secret_token_abc');
});

test('PLUGIN-URL-05: openPluginGateway 错误拦截与降级行为', () => {
  let warningMessage = '';
  const mockToast = {
    warning: (msg) => { warningMessage = msg; }
  };

  const emptyPlugin = { id: 'empty-one', manifest: {} };
  const res = PluginUrlResolver.openPluginGateway(emptyPlugin, '', mockToast);
  assert.equal(res, false);
  assert.match(warningMessage, /未提供可直接访问的 Web 页面接口/);
});
