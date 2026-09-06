// 端点常量与本地存储键 —— API 路径字符串仅允许出现在本文件（前端架构门禁 rule FE-API-OWNERSHIP）
export const API_BASE = `${window.location.origin}/api`;

export const LS_KEYS = {
  token: 'lwt_token',
  user: 'lwt_user',
};

export const API = {
  auth: {
    login: '/Auth/Login',
    check: '/Auth/Check',
    changeCredential: '/Auth/ChangeCredential',
  },
  commands: {
    list: '/Commands',
    item: (id) => `/Commands/${id}`,
    execute: (id) => `/Commands/${id}/Execute`,
    quick: '/Commands/QuickExecute',
    history: (id) => `/Commands/${id}/History`,
  },
  groups: {
    list: '/Groups',
    item: (id) => `/Groups/${id}`,
  },
  schedules: {
    list: '/Schedules',
    item: (id) => `/Schedules/${id}`,
    toggle: (id) => `/Schedules/${id}/Toggle`,
    runNow: (id) => `/Schedules/${id}/RunNow`,
    records: (id) => `/Schedules/${id}/Records`,
  },
  history: {
    list: '/History',
    clear: '/History',
  },
  logs: {
    operations: '/Logs/Operations',
    files: '/Logs/Files',
    file: (name) => `/Logs/Files/${encodeURIComponent(name)}`,
  },
  overview: '/Overview',
  systemStatus: {
    current: '/SystemStatus',
    history: '/SystemStatus/History',
  },
};
