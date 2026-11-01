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
 * 默认单例组合过滤器
 */
const defaultCompositeFilter = new CompositePluginFilter();

/**
 * 统一快捷过滤函数（纯函数，零副作用）
 */
export function filterPlugins(plugins, { profile = 'all', category = 'all', keyword = '' } = {}) {
  return defaultCompositeFilter.filter(plugins, { profile, category, keyword });
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
