/**
 * 插件中心复合检索策略体系（Strategy & Adapter Pattern）
 *
 * 核心目标：
 * 1. 采用策略模式解耦 Profile（运行画像）、Category（业务分类）、Keyword（关键词全文）等多维度过滤；
 * 2. 避免在 View 中书写纠缠内联逻辑，支持多关键词空格拆分（联合匹配）与大小写不敏感检索；
 * 3. 规范检索适配契约，对上层视图暴露统一纯函数与管道调用。
 */

export class ProfileFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.profile || context.profile === 'all') {
      return true;
    }
    if (context.profile === 'resident') {
      return plugin.profile === 0;
    }
    if (context.profile === 'oneshot') {
      return plugin.profile === 1;
    }
    return true;
  }
}

export class CategoryFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.category || context.category === 'all') {
      return true;
    }
    return plugin.manifest?.category === context.category;
  }
}

export class StateFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.state || context.state === 'all') {
      return true;
    }
    const targetState = String(context.state).toLowerCase();
    switch (targetState) {
      case 'running':
        return plugin.state === 2;
      case 'ready':
      case 'standby':
        // 脚本轻量扩展在启用且非异常/缺少依赖时为就绪待命状态
        if (plugin.profile === 1) {
          const isDegradedOrMissing = plugin.state === 3 || plugin.state === 4 || plugin.state === 5 ||
            plugin.state === 7 || plugin.state === 8 || Boolean(plugin.isMissingRuntime || plugin.isMissingDependencies);
          return plugin.state !== 6 && plugin.manifest?.enabled !== false && !isDegradedOrMissing;
        }
        return false;
      case 'degraded':
      case 'error':
        return plugin.state === 3 || plugin.state === 4 || plugin.state === 5;
      case 'missing':
        return plugin.state === 7 || plugin.state === 8 || Boolean(plugin.isMissingRuntime || plugin.isMissingDependencies);
      case 'stopped':
        // 已停止仅匹配常驻微服务人工停止/未启动；脚本轻量扩展若已启用属于 ready，不属于 stopped
        if (plugin.profile === 1) {
          return false;
        }
        return plugin.state === 0;
      case 'disabled':
        // 显式禁用的扩展
        return plugin.state === 6 || plugin.manifest?.enabled === false;
      default:
        return true;
    }
  }
}

export class KeywordFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.keyword) {
      return true;
    }
    const raw = String(context.keyword).trim().toLowerCase();
    if (!raw) {
      return true;
    }

    // 支持以空格分隔的多词联合匹配（所有 token 均须命中某字段）
    const tokens = raw.split(/\s+/).filter(Boolean);
    if (tokens.length === 0) {
      return true;
    }

    const name = String(plugin.name || '').toLowerCase();
    const id = String(plugin.id || '').toLowerCase();
    const desc = String(plugin.manifest?.description || '').toLowerCase();
    const cat = String(plugin.manifest?.category || '').toLowerCase();

    return tokens.every((token) => {
      return (
        name.includes(token) ||
        id.includes(token) ||
        desc.includes(token) ||
        cat.includes(token)
      );
    });
  }
}

export class CompositePluginFilter {
  constructor(strategies) {
    this.strategies = strategies || [
      new ProfileFilterStrategy(),
      new CategoryFilterStrategy(),
      new KeywordFilterStrategy(),
      new StateFilterStrategy(),
    ];
  }

  addStrategy(strategy) {
    if (strategy && typeof strategy.apply === 'function') {
      this.strategies.push(strategy);
    }
    return this;
  }

  filter(plugins, context) {
    if (!Array.isArray(plugins)) {
      return [];
    }
    return plugins.filter((plugin) => {
      return this.strategies.every((s) => s.apply(plugin, context));
    });
  }
}

/**
 * 计算插件状态排序权重（运行中优先置顶）
 * 1: 运行中 (Running)
 * 2: 启动中 (Starting)
 * 3: 异常/降级 (Degraded/Backoff/Crashed)
 * 4: 缺少依赖/环境 (Missing)
 * 5: 已停止/已禁用 (Stopped/Disabled)
 */
export function getPluginStateWeight(plugin) {
  if (!plugin) return 99;
  if (plugin.state === 2) return 1;
  if (plugin.state === 1) return 2;
  if (plugin.profile === 1 && plugin.state !== 6 && plugin.manifest?.enabled !== false && !plugin.isMissingRuntime && !plugin.isMissingDependencies) {
    return 3;
  }
  if (plugin.state === 3 || plugin.state === 4 || plugin.state === 5) return 4;
  if (plugin.state === 7 || plugin.state === 8 || plugin.isMissingRuntime || plugin.isMissingDependencies) return 5;
  if (plugin.state === 0) return 6;
  return 7;
}

/**
 * 按照运行状态优先级对插件进行稳定排序
 */
export function sortPluginsByState(plugins) {
  if (!Array.isArray(plugins)) return [];
  return [...plugins].sort((a, b) => {
    const wa = getPluginStateWeight(a);
    const wb = getPluginStateWeight(b);
    if (wa !== wb) {
      return wa - wb;
    }
    return String(a.id || '').localeCompare(String(b.id || ''));
  });
}

/**
 * 默认单例组合过滤器
 */
const defaultCompositeFilter = new CompositePluginFilter();

/**
 * 统一快捷过滤函数（纯函数，零副作用，默认启动状态置顶）
 */
export function filterPlugins(plugins, { profile = 'all', category = 'all', keyword = '', state = 'all' } = {}) {
  const filtered = defaultCompositeFilter.filter(plugins, { profile, category, keyword, state });
  return sortPluginsByState(filtered);
}

/**
 * 统一插件检索适配器（Adapter Pattern）
 * 隔离本地内存快速索引与远端分页搜索，向视图层提供标准接口
 */
export class PluginSearchAdapter {
  static search(plugins, options) {
    return filterPlugins(plugins, options);
  }
}
