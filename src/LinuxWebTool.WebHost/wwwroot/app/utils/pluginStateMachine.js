/**
 * 插件中心有限状态机与生命周期守卫（Finite State Machine Pattern）
 *
 * 与后端 LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginState 严格对齐：
 * 0: Stopped, 1: Starting, 2: Running, 3: Crashed, 4: Backoff, 5: Degraded, 6: Disabled, 7: MissingRuntime
 */

export const PLUGIN_STATE = {
  STOPPED: 0,
  STARTING: 1,
  RUNNING: 2,
  CRASHED: 3,
  BACKOFF: 4,
  DEGRADED: 5,
  DISABLED: 6,
  MISSING_RUNTIME: 7,
  MISSING_DEPENDENCIES: 8,
};

export const RUNTIME_MAP = {
  0: { label: 'Node.js', icon: '🟢', color: 'text-emerald-400 border-emerald-500/30 bg-emerald-500/10' },
  1: { label: 'Python', icon: '🐍', color: 'text-amber-400 border-amber-500/30 bg-amber-500/10' },
  2: { label: '.NET AOT', icon: '⚡', color: 'text-purple-400 border-purple-500/30 bg-purple-500/10' },
  3: { label: 'Bash', icon: '🐚', color: 'text-sky-400 border-sky-500/30 bg-sky-500/10' },
  4: { label: 'PowerShell', icon: '💻', color: 'text-blue-400 border-blue-500/30 bg-blue-500/10' },
  5: { label: 'Native', icon: '⚙️', color: 'text-slate-300 border-slate-500/30 bg-slate-500/10' },
  6: { label: 'Go', icon: '🐹', color: 'text-cyan-400 border-cyan-500/30 bg-cyan-500/10' },
};

export const STATE_META_MAP = {
  0: { label: '已停止', color: 'bg-slate-700 text-slate-300', isAnimated: false },
  1: { label: '启动中', color: 'bg-amber-500/20 text-amber-300 border border-amber-500/40 animate-pulse', isAnimated: true },
  2: { label: '运行中', color: 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/40', isAnimated: false },
  3: { label: '异常崩溃', color: 'bg-red-500/20 text-red-300 border border-red-500/40', isAnimated: false },
  4: { label: '重试等待', color: 'bg-amber-700/30 text-amber-400 border border-amber-600/50', isAnimated: false },
  5: { label: '降级', color: 'bg-amber-700/30 text-amber-400 border border-amber-600/50', isAnimated: false },
  6: { label: '已禁用', color: 'bg-slate-700 text-slate-400', isAnimated: false },
  7: { label: '缺少环境', color: 'bg-amber-500/20 text-amber-300 border border-amber-500/40', isAnimated: false },
  8: { label: '缺少依赖', color: 'bg-rose-500/20 text-rose-300 border border-rose-500/40', isAnimated: false },
};

export class PluginStateMachine {
  /**
   * 判断插件当前是否允许执行【启动】操作
   */
  static canStart(plugin) {
    if (!plugin) return false;
    if (plugin.isMissingRuntime || plugin.state === PLUGIN_STATE.MISSING_RUNTIME) return false;
    if (plugin.isMissingDependencies || plugin.state === PLUGIN_STATE.MISSING_DEPENDENCIES) return false;
    if (plugin.manifest?.enabled === false) return false;

    // 常驻微服务：已停止、崩溃、重试退避、降级状态下允许启动
    if (plugin.profile === 0) {
      return (
        plugin.state === PLUGIN_STATE.STOPPED ||
        plugin.state === PLUGIN_STATE.CRASHED ||
        plugin.state === PLUGIN_STATE.BACKOFF ||
        plugin.state === PLUGIN_STATE.DEGRADED
      );
    }

    // 一次性脚本：只要未禁用且依赖完整，均可执行启动/触发
    return true;
  }

  /**
   * 判断插件当前是否允许执行【停止】操作
   * 支持运行中、启动中以及降级/崩溃/退避状态下的手动停止与熔断重置
   */
  static canStop(plugin) {
    if (!plugin) return false;
    if (plugin.profile !== 0) return false; // 仅常驻微服务具有长期运行和停止概念
    return (
      plugin.state === PLUGIN_STATE.RUNNING ||
      plugin.state === PLUGIN_STATE.STARTING ||
      plugin.state === PLUGIN_STATE.CRASHED ||
      plugin.state === PLUGIN_STATE.BACKOFF ||
      plugin.state === PLUGIN_STATE.DEGRADED
    );
  }

  /**
   * 判断插件当前是否允许执行【重启】操作
   */
  static canRestart(plugin) {
    if (!plugin) return false;
    if (plugin.profile !== 0) return false;
    if (plugin.isMissingRuntime || plugin.state === PLUGIN_STATE.MISSING_RUNTIME) return false;
    if (plugin.isMissingDependencies || plugin.state === PLUGIN_STATE.MISSING_DEPENDENCIES) return false;
    return plugin.state === PLUGIN_STATE.RUNNING || plugin.state === PLUGIN_STATE.STARTING;
  }

  /**
   * 获取插件的展示状态元数据
   */
  static getStateMeta(plugin) {
    if (!plugin) return STATE_META_MAP[PLUGIN_STATE.STOPPED];

    if (plugin.isMissingDependencies || plugin.state === PLUGIN_STATE.MISSING_DEPENDENCIES) {
      return STATE_META_MAP[PLUGIN_STATE.MISSING_DEPENDENCIES];
    }
    const isMissing = Boolean(plugin.isMissingRuntime || plugin.state === PLUGIN_STATE.MISSING_RUNTIME);
    if (isMissing) {
      return STATE_META_MAP[PLUGIN_STATE.MISSING_RUNTIME];
    }

    // 一次性脚本类
    if (plugin.profile !== 0) {
      return plugin.manifest?.enabled !== false
        ? { label: '已就绪', color: 'bg-sky-500/20 text-sky-300 border border-sky-500/40', isAnimated: false }
        : STATE_META_MAP[PLUGIN_STATE.DISABLED];
    }

    return STATE_META_MAP[plugin.state] || STATE_META_MAP[PLUGIN_STATE.STOPPED];
  }

  /**
   * 获取插件的运行环境元数据
   */
  static getRuntimeMeta(runtime) {
    return RUNTIME_MAP[runtime] || RUNTIME_MAP[5];
  }
}
