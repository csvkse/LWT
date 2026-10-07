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
  webDavMounts: {
    list: '/WebDavMounts',
    support: '/WebDavMounts/Support',
    item: (id) => `/WebDavMounts/${id}`,
    mount: (id) => `/WebDavMounts/${id}/Mount`,
    unmount: (id) => `/WebDavMounts/${id}/Unmount`,
  },
  rcloneMounts: {
    list: '/RcloneMounts',
    support: '/RcloneMounts/Support',
    item: (id) => `/RcloneMounts/${id}`,
    mount: (id) => `/RcloneMounts/${id}/Mount`,
    unmount: (id) => `/RcloneMounts/${id}/Unmount`,
  },
  mountTasks: { item: (id) => `/MountTasks/${id}` },
  files: {
    list: '/Files',
    content: '/Files/Content',
    mkdir: '/Files/Mkdir',
    rename: '/Files/Rename',
    remove: () => '/Files',
    upload: () => '/Files/Upload',
  },
  terminal: {
    support: '/Terminal/Support',
    sessions: '/Terminal/Sessions',
    session: (id) => '/Terminal/Sessions/' + encodeURIComponent(id),
    attachment: (id) => '/Terminal/Sessions/' + encodeURIComponent(id) + '/Attachment',
    ws: (id, ticket, after = 0) => {
      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
      const query = '?v=2&ticket=' + encodeURIComponent(ticket) + '&after=' + encodeURIComponent(after);
      return protocol + '//' + window.location.host + '/api/terminal/ws/' + encodeURIComponent(id) + query;
    },
  },
  transcode: {
    jobs: '/Transcode/Jobs',
    jobCancel: (id) => `/Transcode/Jobs/${id}/Cancel`,
    jobRetry: (id) => `/Transcode/Jobs/Retry/${id}`,
    jobClearFinished: '/Transcode/Jobs/ClearFinished',
    submit: '/Transcode/Submit',
    presets: '/Transcode/Presets',
    presetItem: (id) => `/Transcode/Presets/${id}`,
    presetExport: '/Transcode/Presets/Export',
    presetImport: '/Transcode/Presets/Import',
    watchRules: '/Transcode/WatchRules',
    watchRuleItem: (id) => `/Transcode/WatchRules/${id}`,
    watchRuleToggle: (id) => `/Transcode/WatchRules/${id}/Toggle`,
    detectFfmpeg: '/Transcode/DetectFfmpeg',
  },
  apiKeys: {
    list: '/ApiKeys',
    create: '/ApiKeys',
    update: (id) => `/ApiKeys/${id}`,
    delete: (id) => `/ApiKeys/${id}`,
    toggle: (id) => `/ApiKeys/${id}/Toggle`,
  },
  frp: {
    lines: '/FrpTunnel/Lines',
    lineItem: (id) => `/FrpTunnel/Lines/${id}`,
    lineStart: (id) => `/FrpTunnel/Lines/${id}/Start`,
    lineStop: (id) => `/FrpTunnel/Lines/${id}/Stop`,
    lineLogs: (id) => `/FrpTunnel/Lines/${id}/Logs`,
    config: '/FrpTunnel/Config',
    status: '/FrpTunnel/Status',
    start: '/FrpTunnel/Start',
    stop: '/FrpTunnel/Stop',
    logs: '/FrpTunnel/Logs',
  },
  gateway: {
    routes: '/Gateway/Routes',
    routeItem: (id) => `/Gateway/Routes/${id}`,
    clusters: '/Gateway/Clusters',
    clusterItem: (id) => `/Gateway/Clusters/${id}`,
    websites: '/Gateway/Websites',
    websiteItem: (id) => `/Gateway/Websites/${id}`,
    tcpRoutes: '/Gateway/TcpRoutes',
    tcpRouteItem: (id) => `/Gateway/TcpRoutes/${id}`,
  },
  easytier: {
    nodes: '/EasyTier/Nodes',
    nodeItem: (id) => `/EasyTier/Nodes/${id}`,
    patchConfig: (id) => `/EasyTier/Nodes/${id}/Config`,
    nodeStart: (id) => `/EasyTier/Nodes/${id}/Start`,
    nodeStop: (id) => `/EasyTier/Nodes/${id}/Stop`,
    availablePort: '/EasyTier/AvailablePort',
    engineStatus: '/EasyTier/Engine/Status',
    engineUpgrade: '/EasyTier/Engine/Upgrade',
    githubRelease: '/EasyTier/Engine/Releases',
    installGitHub: '/EasyTier/Engine/InstallGitHub',
  },
};

