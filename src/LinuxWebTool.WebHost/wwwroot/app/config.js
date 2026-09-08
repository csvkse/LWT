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
    resourceHistory: '/SystemStatus/ResourceHistory',
  },
  smbMounts: {
    list: '/SmbMounts',
    support: '/SmbMounts/Support',
    item: (id) => `/SmbMounts/${id}`,
    mount: (id) => `/SmbMounts/${id}/Mount`,
    unmount: (id) => `/SmbMounts/${id}/Unmount`,
  },
  files: {
    list: '/Files',
    content: '/Files/Content',
    mkdir: '/Files/Mkdir',
    rename: '/Files/Rename',
    remove: () => '/Files',
    upload: () => '/Files/Upload',
  },
  transcode: {
    jobs: '/Transcode/Jobs',
    jobCancel: (id) => `/Transcode/Jobs/${id}/Cancel`,
    jobRetry: (id) => `/Transcode/Jobs/Retry/${id}`,
    jobClearFinished: '/Transcode/Jobs/ClearFinished',
    submit: '/Transcode/Submit',
    presets: '/Transcode/Presets',
    presetItem: (id) => `/Transcode/Presets/${id}`,
    watchRules: '/Transcode/WatchRules',
    watchRuleItem: (id) => `/Transcode/WatchRules/${id}`,
    watchRuleToggle: (id) => `/Transcode/WatchRules/${id}/Toggle`,
    detectFfmpeg: '/Transcode/DetectFfmpeg',
  },
};
