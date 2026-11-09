/**
 * LinuxWebTool 统一跨模块导航适配器 (LwtNavigationBridge)
 * 提供环境自省、路径规整、文件/目录解构以及 Iframe / Opener / Standalone 多环境自适应跳转能力。
 */

export class LwtNavigationBridge {
  /**
   * 智能定位至文件管理器
   * @param {Object} opts
   * @param {string} opts.path - 目标文件或目录路径
   * @param {boolean} [opts.isDir=true] - 是否明确为目录（若为 false 则自动提取父目录与聚焦目标）
   * @param {'container'|'host'|string} [opts.target='container'] - 目标命名空间
   */
  static navigateToFile({ path, isDir = true, target = 'container' }) {
    const normalized = this._cleanPath(path);
    let targetDir = normalized;
    let focusFile = '';

    if (!isDir) {
      // 核心防御：若是文件，提取其所在父目录，并将文件名作为 focus 目标传递
      const lastSlash = normalized.lastIndexOf('/');
      if (lastSlash <= 0) {
        targetDir = '/';
        focusFile = normalized.replace(/^\//, '');
      } else {
        targetDir = normalized.slice(0, lastSlash);
        focusFile = normalized.slice(lastSlash + 1);
      }
    }

    const query = {
      path: targetDir || '/',
      target: target === 'host' ? 'host' : 'container',
    };
    if (focusFile) {
      query.focus = focusFile;
    }

    this._dispatch({
      path: '/files',
      query,
    });
  }

  /**
   * 智能建立交互式终端会话
   * @param {Object} opts
   * @param {string} opts.cwd - 终端初始工作目录（若是文件则自动回退至其父目录）
   * @param {boolean} [opts.isDir=true] - 是否为目录
   * @param {'container'|'host'|string} [opts.target='container'] - 目标命名空间
   */
  static navigateToTerminal({ cwd, isDir = true, target = 'container' }) {
    const normalized = this._cleanPath(cwd);
    let targetDir = normalized;

    if (!isDir) {
      const lastSlash = normalized.lastIndexOf('/');
      targetDir = lastSlash <= 0 ? '/' : normalized.slice(0, lastSlash);
    }

    this._dispatch({
      path: '/terminal',
      query: {
        cwd: targetDir || '/',
        target: target === 'host' ? 'host' : 'container',
        open: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), // 注入事务随机令牌，消除历史会话碰撞
      },
    });
  }

  /**
   * 跨上下文自适应通信分发
   */
  static _dispatch(navPayload) {
    const isIframe = typeof window !== 'undefined' && window.parent && window.parent !== window;
    const hasOpener = typeof window !== 'undefined' && window.opener && !window.opener.closed;

    if (isIframe) {
      // 场景 A：处于主系统 Webview 模态框或 Iframe 嵌入中 -> postMessage 协同关闭并跳转
      window.parent.postMessage({
        type: 'LWT_NAVIGATE',
        path: navPayload.path,
        query: navPayload.query,
      }, '*');
      return;
    }

    if (hasOpener) {
      // 场景 B：由主系统 window.open 弹出的独立标签页 -> 尝试通知父窗口聚焦并导航
      try {
        window.opener.postMessage({
          type: 'LWT_NAVIGATE',
          path: navPayload.path,
          query: navPayload.query,
        }, '*');
        window.opener.focus();
        return;
      } catch (_) {
        // 跨域或安全限制降级至自身重定向
      }
    }

    // 场景 C：纯独立标签页直接访问（如从浏览器书签或直接敲端口进入）
    // 自动自省计算主系统的 SPA 基准入口：/app/#<path>?<query>
    const searchParams = new URLSearchParams();
    for (const [k, v] of Object.entries(navPayload.query || {})) {
      if (v !== undefined && v !== null && v !== '') searchParams.set(k, String(v));
    }
    const queryString = searchParams.toString();
    const finalHash = `#${navPayload.path}${queryString ? '?' + queryString : ''}`;

    if (typeof window !== 'undefined') {
      window.location.href = `/app/${finalHash}`;
    }
  }

  static _cleanPath(p) {
    if (!p) return '/';
    return String(p).replace(/[\\]+/g, '/').replace(/\/+/g, '/');
  }
}
