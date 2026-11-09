import assert from 'node:assert/strict';
import { test } from 'node:test';
import { LwtNavigationBridge } from '../../plugins/disk-explorer/ui/navigationBridge.js';

test('NAV-01: navigateToFile should decompose file path into parent dir and focus parameter', () => {
  let dispatched = null;
  const originalDispatch = LwtNavigationBridge._dispatch;
  LwtNavigationBridge._dispatch = (payload) => {
    dispatched = payload;
  };

  try {
    // 场景 1: 多级目录下的文件
    LwtNavigationBridge.navigateToFile({
      path: '/app/mcp-bridge',
      isDir: false,
      target: 'container',
    });
    assert.ok(dispatched, 'Should dispatch navigation payload');
    assert.equal(dispatched.path, '/files');
    assert.equal(dispatched.query.path, '/app');
    assert.equal(dispatched.query.focus, 'mcp-bridge');
    assert.equal(dispatched.query.target, 'container');

    // 场景 2: 根目录下的文件
    LwtNavigationBridge.navigateToFile({
      path: '/Scalar.AspNetCore.dll',
      isDir: false,
      target: 'container',
    });
    assert.equal(dispatched.path, '/files');
    assert.equal(dispatched.query.path, '/');
    assert.equal(dispatched.query.focus, 'Scalar.AspNetCore.dll');

    // 场景 3: 宿主文件系统中的深层文件
    LwtNavigationBridge.navigateToFile({
      path: '/var/log/journal/system.journal',
      isDir: false,
      target: 'host',
    });
    assert.equal(dispatched.path, '/files');
    assert.equal(dispatched.query.path, '/var/log/journal');
    assert.equal(dispatched.query.focus, 'system.journal');
    assert.equal(dispatched.query.target, 'host');
  } finally {
    LwtNavigationBridge._dispatch = originalDispatch;
  }
});

test('NAV-02: navigateToFile should preserve path without focus when isDir is true', () => {
  let dispatched = null;
  const originalDispatch = LwtNavigationBridge._dispatch;
  LwtNavigationBridge._dispatch = (payload) => {
    dispatched = payload;
  };

  try {
    LwtNavigationBridge.navigateToFile({
      path: '/app/data/plugins',
      isDir: true,
      target: 'container',
    });
    assert.ok(dispatched);
    assert.equal(dispatched.path, '/files');
    assert.equal(dispatched.query.path, '/app/data/plugins');
    assert.equal(dispatched.query.focus, undefined);
    assert.equal(dispatched.query.target, 'container');

    // 根目录
    LwtNavigationBridge.navigateToFile({
      path: '/',
      isDir: true,
      target: 'host',
    });
    assert.equal(dispatched.path, '/files');
    assert.equal(dispatched.query.path, '/');
    assert.equal(dispatched.query.focus, undefined);
    assert.equal(dispatched.query.target, 'host');
  } finally {
    LwtNavigationBridge._dispatch = originalDispatch;
  }
});

test('NAV-03: navigateToTerminal should inject unique open nonce and fallback file to parent dir', () => {
  let dispatched = null;
  const originalDispatch = LwtNavigationBridge._dispatch;
  LwtNavigationBridge._dispatch = (payload) => {
    dispatched = payload;
  };

  try {
    // 目录终端
    LwtNavigationBridge.navigateToTerminal({
      cwd: '/var/log',
      isDir: true,
      target: 'host',
    });
    assert.ok(dispatched);
    assert.equal(dispatched.path, '/terminal');
    assert.equal(dispatched.query.cwd, '/var/log');
    assert.equal(dispatched.query.target, 'host');
    assert.ok(typeof dispatched.query.open === 'string' && dispatched.query.open.length >= 4, 'Nonce should be generated');

    // 针对文件的终端导航应自动降级至父目录
    LwtNavigationBridge.navigateToTerminal({
      cwd: '/app/mcp-bridge',
      isDir: false,
      target: 'container',
    });
    assert.equal(dispatched.path, '/terminal');
    assert.equal(dispatched.query.cwd, '/app');
    assert.equal(dispatched.query.target, 'container');
    assert.ok(dispatched.query.open);
  } finally {
    LwtNavigationBridge._dispatch = originalDispatch;
  }
});

test('NAV-04: Path normalization should clean backslashes and redundant slashes', () => {
  assert.equal(LwtNavigationBridge._cleanPath('C:\\app\\data\\'), 'C:/app/data/');
  assert.equal(LwtNavigationBridge._cleanPath('/app///data//plugins/'), '/app/data/plugins/');
  assert.equal(LwtNavigationBridge._cleanPath(''), '/');
  assert.equal(LwtNavigationBridge._cleanPath(null), '/');
});

test('NAV-05: _dispatch should adapt across Iframe, Opener, and Standalone tab environments', () => {
  // 模拟场景 A: Iframe 环境
  const iframeMessages = [];
  const fakeParent = {
    postMessage: (msg, origin) => {
      iframeMessages.push({ msg, origin });
    },
  };
  globalThis.window = {
    parent: fakeParent,
  };

  LwtNavigationBridge._dispatch({
    path: '/files',
    query: { path: '/app', target: 'container', focus: 'mcp-bridge' },
  });

  assert.equal(iframeMessages.length, 1);
  assert.equal(iframeMessages[0].msg.type, 'LWT_NAVIGATE');
  assert.equal(iframeMessages[0].msg.path, '/files');
  assert.equal(iframeMessages[0].msg.query.focus, 'mcp-bridge');
  assert.equal(iframeMessages[0].origin, '*');

  // 模拟场景 B: Standalone 独立标签页环境 (window.parent === window)
  let assignedHref = '';
  globalThis.window = {
    parent: null,
    opener: null,
    location: {
      set href(val) {
        assignedHref = val;
      },
    },
  };
  globalThis.window.parent = globalThis.window; // top window

  LwtNavigationBridge._dispatch({
    path: '/terminal',
    query: { cwd: '/var/log', target: 'host', open: 'test-nonce-123' },
  });

  assert.ok(assignedHref.startsWith('/app/#/terminal?'), 'Should redirect to SPA hash URL');
  assert.ok(assignedHref.includes('cwd=%2Fvar%2Flog'));
  assert.ok(assignedHref.includes('target=host'));
  assert.ok(assignedHref.includes('open=test-nonce-123'));

  delete globalThis.window;
});
